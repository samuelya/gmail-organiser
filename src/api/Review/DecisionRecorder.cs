using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;

namespace GmailOrganiser.Review;

/// <summary>
/// Adds one <c>decisions</c> row per approve or reject to the caller's unit of work; older rows are never updated.
/// The caller saves it together with the status change.
/// </summary>
public sealed class DecisionRecorder(AppDbContext db, TimeProvider time)
{
    public ValueTask RecordAsync(SuggestionRow suggestion, MessageRow message, DecisionOutcome outcome, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        db.Decisions.Add(new DecisionRow
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
        });
        return ValueTask.CompletedTask;
    }
}
