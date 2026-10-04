using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Review;

/// <summary>
/// Adds one <c>decisions</c> row per approve or reject to the caller's unit of work; the caller saves it together with
/// the status change and then calls <see cref="Committed"/>, which wakes the background embedding: the request never
/// waits on the model. Rows are otherwise only updated with their vector. Whether a document-type parent is set is read
/// once per recorder (one request).
/// </summary>
public sealed partial class DecisionRecorder(
    AppDbContext db, ISettingsStore settings, IDecisionEmbeddingQueue embedding, TimeProvider time, ILogger<DecisionRecorder> logger)
{
    private bool recorded;
    private bool? typeDecided;

    public async ValueTask RecordAsync(SuggestionRow suggestion, MessageRow message, DecisionOutcome outcome, CancellationToken ct)
    {
        typeDecided ??= (await settings.GetAsync(ct)).DocumentTypeParent is not null;
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
            DocumentTypeDecided = typeDecided.Value,
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
