using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <summary>
/// Stores Gmail metadata in <c>messages</c> keyed by the Gmail message ID: new ids are inserted, known ids get their
/// mutable fields refreshed. <c>analysis_status</c> and <c>fetched_at</c> of existing rows are never touched.
/// </summary>
public sealed class MessageUpserter(AppDbContext db, TimeProvider time)
{
    private static readonly Dictionary<string, MessageCategory> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CATEGORY_PERSONAL"] = MessageCategory.Primary,
        ["CATEGORY_SOCIAL"] = MessageCategory.Social,
        ["CATEGORY_PROMOTIONS"] = MessageCategory.Promotions,
        ["CATEGORY_UPDATES"] = MessageCategory.Updates,
        ["CATEGORY_FORUMS"] = MessageCategory.Forums,
    };

    /// <summary>Upserts <paramref name="messages"/> in one <c>SaveChanges</c> and returns the stored rows.</summary>
    public async Task<IReadOnlyList<MessageRow>> UpsertAsync(IReadOnlyList<GmailMessageMetadata> messages, CancellationToken ct)
    {
        var unique = messages.DistinctBy(m => m.Id, StringComparer.Ordinal).ToList();
        if (unique.Count == 0)
        {
            return [];
        }

        var ids = unique.Select(m => m.Id).ToList();
        var existing = await db.Messages.Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        var now = time.GetUtcNow();
        var rows = new List<MessageRow>(unique.Count);
        foreach (var metadata in unique)
        {
            if (existing.TryGetValue(metadata.Id, out var row))
            {
                Refresh(row, metadata, now);
            }
            else
            {
                row = Create(metadata, now);
                db.Messages.Add(row);
            }

            rows.Add(row);
        }

        await db.SaveChangesAsync(ct);
        return rows;
    }

    public static MessageCategory? CategoryOf(IEnumerable<string> labelIds) =>
        labelIds.Select(l => Categories.TryGetValue(l, out var c) ? c : (MessageCategory?)null).FirstOrDefault(c => c is not null);

    private static MessageRow Create(GmailMessageMetadata m, DateTimeOffset now)
    {
        var from = GmailMetadataMapper.ParseFrom(m.From);
        var row = new MessageRow
        {
            Id = m.Id,
            ThreadId = m.ThreadId,
            FromAddress = from.Address,
            FromName = from.DisplayName,
            ToHeader = m.To,
            Subject = m.Subject,
            InternalDate = m.InternalDate.ToUniversalTime(),
            FetchedAt = now,
        };
        Refresh(row, m, now);
        return row;
    }

    private static void Refresh(MessageRow row, GmailMessageMetadata m, DateTimeOffset now)
    {
        row.LabelIds = [.. m.LabelIds];
        row.Category = CategoryOf(m.LabelIds);
        row.HistoryId = string.IsNullOrEmpty(m.HistoryId) ? row.HistoryId : m.HistoryId;
        row.Snippet = m.Snippet;
        row.SizeEstimate = m.SizeEstimate;
        row.HasAttachment = m.HasAttachment;
        row.ListId = m.ListId;
        row.ListUnsubscribe = m.ListUnsubscribe;
        row.DeletedInGmail = false;
        row.UpdatedAt = now;
    }
}
