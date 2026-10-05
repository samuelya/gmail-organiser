using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Senders;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <summary>
/// Stores Gmail metadata in <c>messages</c> keyed by the Gmail message ID: new ids are inserted, known ids get their
/// mutable fields refreshed. <c>analysis_status</c> and <c>fetched_at</c> of existing rows are never touched, and a
/// row whose Gmail metadata is unchanged is not written at all (<c>updated_at</c> means "changed in Gmail").
/// An inserted message updates its thread's <c>thread_replied</c> (#177): <c>SENT</c> makes the thread replied, any
/// other message makes a stored "not replied" unknown again; "replied" is final. Every stored row gets its canonical
/// (relay-decoded) sender.
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
        var newThreads = unique.Where(m => !existing.ContainsKey(m.Id)).Select(m => m.ThreadId).Distinct(StringComparer.Ordinal).ToArray();
        var sentThreads = unique
            .Where(m => !existing.ContainsKey(m.Id) && m.LabelIds.Contains(MessageProtection.SentLabel, StringComparer.Ordinal))
            .Select(m => m.ThreadId)
            .ToHashSet(StringComparer.Ordinal);
        var repliedThreads = newThreads.Length == 0
            ? []
            : (await db.Messages
                .Where(m => newThreads.Contains(m.ThreadId) && m.ThreadReplied == true)
                .Select(m => m.ThreadId)
                .Distinct()
                .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        repliedThreads.UnionWith(sentThreads);
        var now = time.GetUtcNow();
        var rows = new List<MessageRow>(unique.Count);
        foreach (var metadata in unique)
        {
            if (existing.TryGetValue(metadata.Id, out var row))
            {
                var entry = db.Entry(row);
                Refresh(row, metadata);
                entry.DetectChanges();
                if (entry.State == EntityState.Modified)
                {
                    row.UpdatedAt = now;
                }

                // After the check: a row decoded only now (stored before the decoder) has not changed in Gmail.
                SetCanonical(row);
            }
            else
            {
                row = Create(metadata, now);
                row.ThreadReplied = repliedThreads.Contains(row.ThreadId) ? true : null;
                db.Messages.Add(row);
            }

            rows.Add(row);
        }

        await db.SaveChangesAsync(ct);
        await UpdateThreadsAsync([.. sentThreads], [.. newThreads.Where(t => !repliedThreads.Contains(t))], ct);
        return rows;
    }

    /// <summary>Marks <paramref name="replied"/> threads replied and makes the stored "not replied" of <paramref name="reopened"/> unknown.</summary>
    private async Task UpdateThreadsAsync(string[] replied, string[] reopened, CancellationToken ct)
    {
        if (replied.Length > 0)
        {
            await db.Messages
                .Where(m => replied.Contains(m.ThreadId) && m.ThreadReplied != true)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.ThreadReplied, true), ct);
        }

        if (reopened.Length > 0)
        {
            await db.Messages
                .Where(m => reopened.Contains(m.ThreadId) && m.ThreadReplied == false)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.ThreadReplied, (bool?)null), ct);
        }
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
            UpdatedAt = now,
        };
        Refresh(row, m);
        SetCanonical(row);
        return row;
    }

    /// <summary>Sets the relay-decoded sender from <see cref="MessageRow.FromAddress"/>.</summary>
    public static void SetCanonical(MessageRow row)
    {
        var canonical = RelayAddressDecoder.Decode(row.FromAddress);
        row.CanonicalAddress = canonical.CanonicalAddress;
        row.CanonicalDomain = canonical.CanonicalDomain;
    }

    private static void Refresh(MessageRow row, GmailMessageMetadata m)
    {
        row.LabelIds = [.. m.LabelIds];
        row.Category = CategoryOf(m.LabelIds);
        row.HistoryId = string.IsNullOrEmpty(m.HistoryId) ? row.HistoryId : m.HistoryId;
        row.Snippet = m.Snippet;
        row.SizeEstimate = m.SizeEstimate;
        row.HasAttachment = m.HasAttachment;
        row.ListId = m.ListId;
        row.ListUnsubscribe = m.ListUnsubscribe;
        // Mail in Trash is not live anywhere (stats, analysis, apply, patterns), however it got there; out of Trash it is again.
        row.DeletedInGmail = m.LabelIds.Contains(MailboxFetchJob.TrashLabelId, StringComparer.OrdinalIgnoreCase);
    }
}
