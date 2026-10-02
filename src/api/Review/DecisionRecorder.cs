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
            SubjectTemplate = SubjectTemplate(suggestion.GroupKey) ?? SubjectNormaliser.Template(message.Subject),
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

    /// <summary>The subject template of a <see cref="GroupKey"/>: what follows <c>list:id|</c> or <c>from:address|category|</c>.</summary>
    public static string? SubjectTemplate(string? groupKey)
    {
        if (groupKey is null)
        {
            return null;
        }

        var separators = groupKey.StartsWith(GroupKey.ListPrefix, StringComparison.Ordinal) ? 1
            : groupKey.StartsWith(GroupKey.FromPrefix, StringComparison.Ordinal) ? 2
            : 0;
        var start = 0;
        for (var i = 0; i < separators; i++)
        {
            var bar = groupKey.IndexOf('|', start);
            if (bar < 0)
            {
                return null;
            }

            start = bar + 1;
        }

        return separators == 0 ? null : groupKey[start..];
    }
}
