using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Policies;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>
/// Adds one <c>decisions</c> row per approve or reject to the caller's unit of work; the caller saves it together with
/// the status change and then calls <see cref="Committed"/>, which wakes the background embedding: the request never
/// waits on the model. Rows are otherwise only updated with their vector. The document type is decided under the
/// parent of the suggestion's run (its snapshot, not the current Settings), read once per run per recorder (one request).
/// Unedited Stage-0 suggestions are rule-made, not the user's per-message teaching, and are not recorded; an edited
/// Stage-0 card is the user's own outcome and is (#349). An approval also proposes a learned sub-rule for a mixed
/// sender's policy (<see cref="LearnedRuleProposer"/>, #361); null in tests that cover only the memory.
/// </summary>
public sealed partial class DecisionRecorder(
    AppDbContext db, IDecisionEmbeddingQueue embedding, TimeProvider time, ILogger<DecisionRecorder> logger,
    LearnedRuleProposer? learned = null)
{
    private readonly Dictionary<Guid, string?> runParents = [];
    private bool recorded;

    /// <summary>
    /// Before the approvals of a group or bulk decision: locks the mixed policies they may teach, in one fixed order
    /// (<see cref="LearnedRuleProposer.LockAsync"/>), so concurrent decisions never deadlock on them.
    /// </summary>
    public ValueTask LockPoliciesAsync(
        IEnumerable<(SuggestionRow Suggestion, MessageRow Message)> changes, DecisionOutcome outcome, CancellationToken ct) =>
        outcome == DecisionOutcome.Approved && learned is not null ? learned.LockAsync(changes, ct) : ValueTask.CompletedTask;

    public async ValueTask RecordAsync(SuggestionRow suggestion, MessageRow message, DecisionOutcome outcome, CancellationToken ct)
    {
        if (outcome == DecisionOutcome.Approved && learned is not null)
        {
            await learned.ProposeAsync(suggestion, message, ct);
        }

        if (suggestion.Source == SuggestionSource.Stage0 && !suggestion.Edited)
        {
            return;
        }

        var parent = await RunParentAsync(suggestion.RunId, ct);
        var now = time.GetUtcNow();
        var row = new DecisionRow
        {
            Id = Guid.CreateVersion7(now),
            MessageId = suggestion.MessageId,
            SenderAddress = suggestion.SenderAddress,
            ListId = GroupKey.NormaliseListId(message.ListId),
            ScopeKey = GroupKey.For(message),
            // The message's own template: the one GroupKey.For put in its group key, without parsing the key.
            SubjectTemplate = SubjectNormaliser.Template(message.Subject),
            TopicLabel = suggestion.TopicLabel,
            DocumentTypeLabel = suggestion.DocumentTypeLabel,
            DocumentTypeDecided = parent is not null,
            DocumentTypeParent = parent,
            NeedsAction = suggestion.NeedsAction,
            ToBeDeleted = suggestion.ToBeDeleted,
            Outcome = outcome,
            Source = suggestion.Source,
            Edited = suggestion.Edited,
            CreatedAt = now,
        };
        db.Decisions.Add(row);
        recorded = true;
    }

    /// <summary>The run's document-type parent; null without a run (deleted, or a suggestion made outside one).</summary>
    private async ValueTask<string?> RunParentAsync(Guid? runId, CancellationToken ct)
    {
        if (runId is not { } id)
        {
            return null;
        }

        if (!runParents.TryGetValue(id, out var parent))
        {
            parent = await db.AnalysisRuns.AsNoTracking().Where(r => r.Id == id).Select(r => r.DocumentTypeParent).FirstOrDefaultAsync(ct);
            runParents[id] = parent;
        }

        return parent;
    }

    /// <summary>
    /// Post-commit work, best effort: wakes the background embedding when rows were recorded since the last call. Never
    /// throws, so a group or bulk decision never stops partway on it.
    /// </summary>
    public void Committed()
    {
        if (!recorded)
        {
            return;
        }

        recorded = false;
        try
        {
            embedding.Notify();
        }
        catch (Exception ex)
        {
            LogPostCommitFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Post-commit work for recorded decisions failed; the embedding retries later")]
    private static partial void LogPostCommitFailed(ILogger logger, Exception exception);
}
