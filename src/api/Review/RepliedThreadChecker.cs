using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>
/// Finds out whether the user replied in a message's thread (DESIGN §6.4, #177): a thread is replied when any of its
/// messages carries <c>SENT</c>. Checked at mark time only. Unknown threads are first resolved from the stored
/// messages, then with one <c>threads.get</c> each; every result is stored on all of the thread's rows right away, so a
/// rate limit midway loses nothing and a later check asks Gmail only about threads still unknown.
/// </summary>
public sealed class RepliedThreadChecker(AppDbContext db, IGmailClient gmail)
{
    /// <summary>Sets <see cref="MessageRow.ThreadReplied"/> on every row of <paramref name="messages"/> that has none.</summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting; the threads checked so far are stored.</exception>
    public async Task CheckAsync(IReadOnlyCollection<MessageRow> messages, CancellationToken ct)
    {
        var unknown = messages.Where(m => m.ThreadReplied is null).ToList();
        if (unknown.Count == 0)
        {
            return;
        }

        var threadIds = unknown.Select(m => m.ThreadId).Distinct(StringComparer.Ordinal).ToArray();
        var known = await db.Messages
            .Where(m => threadIds.Contains(m.ThreadId) && m.ThreadReplied != null)
            .Select(m => new { m.ThreadId, Replied = m.ThreadReplied!.Value })
            .ToListAsync(ct);
        var results = known
            .GroupBy(k => k.ThreadId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Any(k => k.Replied), StringComparer.Ordinal);

        var sentLocally = await db.Messages
            .Where(m => threadIds.Contains(m.ThreadId) && m.LabelIds.Contains(MessageProtection.SentLabel))
            .Select(m => m.ThreadId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var threadId in sentLocally.Where(t => !results.GetValueOrDefault(t)))
        {
            await StoreAsync(threadId, replied: true, ct);
            results[threadId] = true;
        }

        try
        {
            foreach (var threadId in threadIds.Where(t => !results.ContainsKey(t)))
            {
                // Gmail no longer knowing the thread means nothing in it was sent from this mailbox.
                var summary = await gmail.GetThreadSummaryAsync(threadId, ct);
                var replied = summary?.Messages.Any(m => m.LabelIds.Contains(MessageProtection.SentLabel, StringComparer.Ordinal)) ?? false;
                results[threadId] = await StoreAsync(threadId, replied, ct);
            }
        }
        finally
        {
            foreach (var message in unknown)
            {
                message.ThreadReplied = results.TryGetValue(message.ThreadId, out var replied) ? replied : null;
            }
        }
    }

    /// <summary>
    /// Stores the result on the thread's rows. <c>true</c> is final; <c>false</c> fills only unknown rows, so a reply
    /// fetched meanwhile wins. Returns what the thread is now.
    /// </summary>
    private async Task<bool> StoreAsync(string threadId, bool replied, CancellationToken ct)
    {
        if (replied)
        {
            await db.Messages
                .Where(m => m.ThreadId == threadId && m.ThreadReplied != true)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.ThreadReplied, true), ct);
            return true;
        }

        await db.Messages
            .Where(m => m.ThreadId == threadId && m.ThreadReplied == null)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.ThreadReplied, false), ct);
        return await db.Messages.AnyAsync(m => m.ThreadId == threadId && m.ThreadReplied == true, ct);
    }
}
