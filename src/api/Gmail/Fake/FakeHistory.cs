using System.Globalization;
using System.Text;

namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// The fake mailbox's <c>history.list</c> log: records in ascending id order, of which only the last
/// <see cref="Retention"/> are kept. A start id older than the oldest kept record is expired, as Gmail answers 404.
/// Not thread-safe; <see cref="FakeGmailClient"/> calls it under its lock.
/// </summary>
internal sealed class FakeHistory(long floor)
{
    private const string PageTokenPrefix = "fake-history:";

    private readonly List<HistoryRecord> records = [];

    /// <summary>The oldest start id that can still be answered.</summary>
    private long floor = floor;

    public int Retention { get; set; } = 200;

    public void Append(HistoryRecord record)
    {
        records.Add(record);
        Trim();
    }

    public void Trim()
    {
        while (records.Count > Math.Max(0, Retention))
        {
            floor = Parse(records[0].Id);
            records.RemoveAt(0);
        }
    }

    /// <exception cref="ArgumentException"><paramref name="startHistoryId"/> is not a history ID.</exception>
    /// <exception cref="GmailHistoryExpiredException"><paramref name="startHistoryId"/> is older than the kept history.</exception>
    /// <exception cref="GmailInvalidPageTokenException"><paramref name="pageToken"/> is not one this log issued.</exception>
    public (IReadOnlyList<HistoryRecord> Records, string? NextPageToken) Page(string startHistoryId, string? pageToken, int pageSize)
    {
        if (!long.TryParse(startHistoryId, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            throw new ArgumentException($"'{startHistoryId}' is not a Gmail history ID.", nameof(startHistoryId));
        }

        if (start < floor)
        {
            throw new GmailHistoryExpiredException("The fake mailbox no longer keeps history from that id.");
        }

        var after = Math.Max(start, DecodePageToken(pageToken));
        var remaining = records.Where(r => Parse(r.Id) > after).ToList();
        var page = remaining.Take(pageSize).ToList();
        var next = remaining.Count > page.Count ? EncodePageToken(Parse(page[^1].Id)) : null;
        return (page, next);
    }

    private static long Parse(string id) => long.Parse(id, CultureInfo.InvariantCulture);

    private static string EncodePageToken(long after) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(PageTokenPrefix + after.ToString(CultureInfo.InvariantCulture)));

    private static long DecodePageToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            if (text.StartsWith(PageTokenPrefix, StringComparison.Ordinal)
                && long.TryParse(text[PageTokenPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var after))
            {
                return after;
            }
        }
        catch (FormatException)
        {
        }

        throw new GmailInvalidPageTokenException("Invalid history page token.");
    }
}
