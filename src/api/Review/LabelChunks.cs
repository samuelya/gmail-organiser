using System.Net;
using GmailOrganiser.Gmail;
using Google;

namespace GmailOrganiser.Review;

/// <summary>
/// The <c>batchModify</c> mechanics apply and undo share: messages with identical label changes go together in chunks
/// of at most the cap, and when Gmail refuses a chunk the ids it refuses on their own are isolated by splitting.
/// </summary>
public static class LabelChunks
{
    public const string NotFoundReason = "not found in Gmail";
    public const string RefusedReason = "refused by Gmail";

    /// <summary>Groups <paramref name="items"/> by their (sorted) added and removed label ids, in chunks of at most <paramref name="cap"/>.</summary>
    public static IEnumerable<(IReadOnlyList<T> Items, string[] Add, string[] Remove)> Group<T>(
        IEnumerable<T> items, Func<T, IReadOnlyList<string>> add, Func<T, IReadOnlyList<string>> remove, int cap) =>
        items
            .GroupBy(i => (Add: Key(add(i)), Remove: Key(remove(i))))
            .SelectMany(g => g.Chunk(cap).Select(c => ((IReadOnlyList<T>)c, Sorted(add(g.First())), Sorted(remove(g.First())))));

    /// <summary>
    /// <paramref name="ids"/> failed together with <paramref name="failure"/>: sends each half, splitting again on a 400
    /// or 404, until single refused ids remain; those go to <paramref name="refused"/> with their reason.
    /// <paramref name="sent"/> runs after every part Gmail accepted.
    /// </summary>
    public static async Task IsolateAsync(
        IGmailClient gmail,
        string[] ids,
        string[] add,
        string[] remove,
        GoogleApiException failure,
        Dictionary<string, string> refused,
        Action sent,
        CancellationToken ct)
    {
        if (ids.Length == 1)
        {
            refused[ids[0]] = failure.HttpStatusCode == HttpStatusCode.NotFound ? NotFoundReason : RefusedReason;
            return;
        }

        foreach (var half in new[] { ids[..(ids.Length / 2)], ids[(ids.Length / 2)..] })
        {
            try
            {
                await gmail.BatchModifyAsync(half, add, remove, ct);
                sent();
            }
            catch (GoogleApiException ex) when (IsBadIdOrLabel(ex))
            {
                await IsolateAsync(gmail, half, add, remove, ex, refused, sent, ct);
            }
        }
    }

    /// <summary>
    /// After isolation: when Gmail answered 400 for every id of a chunk of several, the call itself is the problem
    /// (a label, not a few ids), so the chunk fails rather than every message being skipped.
    /// </summary>
    public static void ThrowIfCallRefused(string[] ids, Dictionary<string, string> refused, GoogleApiException failure)
    {
        if (ids.Length > 1 && refused.Count == ids.Length && refused.Values.All(r => r == RefusedReason))
        {
            refused.Clear();
            throw failure;
        }
    }

    /// <summary>Gmail refused the call before changing anything: rate-limited on every attempt, not connected, or a 4xx.</summary>
    public static bool NothingChanged(Exception ex) => ex
        is GmailRateLimitedException
        or GmailNotConnectedException
        or ArgumentException
        or GoogleApiException { HttpStatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError };

    /// <summary>What Gmail answers for an unknown message or label id.</summary>
    public static bool IsBadIdOrLabel(GoogleApiException ex) =>
        ex.HttpStatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

    /// <summary><paramref name="before"/> with <paramref name="remove"/> removed and <paramref name="add"/> added.</summary>
    public static string[] After(string[] before, IReadOnlyList<string> add, IReadOnlyList<string> remove) =>
        [.. before.Except(remove, StringComparer.Ordinal).Concat(add.Except(before, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)];

    public static string[] Sorted(IEnumerable<string> ids) => [.. ids.Order(StringComparer.Ordinal)];

    private static string Key(IEnumerable<string> ids) => string.Join('\n', Sorted(ids));
}
