using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;

namespace GmailOrganiser.Review;

/// <summary>
/// Adds one <c>decisions</c> row per approve or reject to the caller's unit of work; the caller saves it together with
/// the status change. After the commit, <see cref="EmbedRecordedAsync"/> adds the vectors in one model call, so the
/// model's latency never holds the review's row locks. Rows are otherwise never updated.
/// </summary>
public sealed class DecisionRecorder(AppDbContext db, IDecisionMemory memory, TimeProvider time)
{
    private readonly List<DecisionRow> recorded = [];

    public ValueTask RecordAsync(SuggestionRow suggestion, MessageRow message, DecisionOutcome outcome, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var row = new DecisionRow
        {
            Id = Guid.CreateVersion7(now),
            MessageId = suggestion.MessageId,
            SenderAddress = suggestion.SenderAddress,
            ListId = message.ListId,
            // The message's own template: the one GroupKey.For put in its group key, without parsing the key.
            SubjectTemplate = SubjectNormaliser.Template(message.Subject),
            TopicLabel = suggestion.TopicLabel,
            NeedsAction = suggestion.NeedsAction,
            ToBeDeleted = suggestion.ToBeDeleted,
            Outcome = outcome,
            Source = suggestion.Source,
            Edited = suggestion.Edited,
            CreatedAt = now,
        };
        db.Decisions.Add(row);
        recorded.Add(row);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Embeds the rows recorded since the last call and saves the vectors; call after the decisions are committed. A
    /// row stays without a vector when no embedding model is configured or the model fails.
    /// </summary>
    public async Task EmbedRecordedAsync(CancellationToken ct)
    {
        if (recorded.Count == 0)
        {
            return;
        }

        List<DecisionRow> rows = [.. recorded];
        recorded.Clear();
        await memory.EmbedAsync(rows, ct);
        if (rows.Any(r => r.Embedding is not null))
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
