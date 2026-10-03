using System.Collections.Concurrent;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.CleanUp.Unsubscribe;

public enum UnsubscribeOutcome
{
    Ok,
    SenderNotFound,
    NotOneClick,
    InProgress,
}

/// <summary>The senders with a one-click POST in flight (one per sender); a singleton shared by every request scope.</summary>
public sealed class UnsubscribeInFlight
{
    private readonly ConcurrentDictionary<string, byte> senders = new(StringComparer.Ordinal);

    public bool TryEnter(string address) => senders.TryAdd(address, 0);

    public void Exit(string address) => senders.TryRemove(address, out _);
}

/// <summary>
/// Unsubscribe per sender (DESIGN §6.4, epic #24 Q-X1): reads the newest stored <c>List-Unsubscribe</c>, performs the
/// RFC 8058 one-click POST, and records a link or <c>mailto:</c> the user opened by hand. Never labels or trashes mail.
/// </summary>
public sealed class UnsubscribeService(
    AppDbContext db,
    IGmailClient gmail,
    IUnsubscribeSender sender,
    UnsubscribeInFlight inFlight,
    TimeProvider time)
{
    /// <summary>How many of the sender's newest messages are tried when Gmail no longer knows one.</summary>
    public const int MaxMessageTries = 3;

    /// <summary>Null when the sender is unknown.</summary>
    /// <exception cref="GmailNotConnectedException">A https target needs the live <c>List-Unsubscribe-Post</c> header.</exception>
    public async Task<UnsubscribeInfoDto?> GetInfoAsync(string address, CancellationToken ct)
    {
        var row = await db.Senders.AsNoTracking().FirstOrDefaultAsync(s => s.Address == address, ct);
        if (row is null)
        {
            return null;
        }

        var (option, messageId) = await ResolveAsync(address, ct);
        return new UnsubscribeInfoDto(option?.Method, option?.Url, messageId, row.UnsubscribedAt, row.UnsubscribeMethod);
    }

    /// <summary>The one-click POST; on a 2xx answer the sender is marked unsubscribed via one-click.</summary>
    public async Task<(UnsubscribeOutcome Outcome, UnsubscribeResultDto? Result)> SendAsync(string address, CancellationToken ct)
    {
        if (!await db.Senders.AnyAsync(s => s.Address == address, ct))
        {
            return (UnsubscribeOutcome.SenderNotFound, null);
        }

        if (!inFlight.TryEnter(address))
        {
            return (UnsubscribeOutcome.InProgress, null);
        }

        try
        {
            var (option, _) = await ResolveAsync(address, ct);
            if (option is not { Method: UnsubscribeMethod.OneClick })
            {
                return (UnsubscribeOutcome.NotOneClick, null);
            }

            var sent = await sender.SendOneClickAsync(new Uri(option.Url), ct);
            if (sent.Done)
            {
                await MarkRowAsync(address, UnsubscribeMethod.OneClick, ct);
            }

            return (UnsubscribeOutcome.Ok, new UnsubscribeResultDto(
                sent.Done ? UnsubscribeResultDto.Done : UnsubscribeResultDto.Failed, sent.HttpStatus));
        }
        finally
        {
            inFlight.Exit(address);
        }
    }

    /// <summary>Records that the user opened a link or <c>mailto:</c> by hand; false when the sender is unknown.</summary>
    public Task<bool> MarkAsync(string address, UnsubscribeMethod method, CancellationToken ct) =>
        MarkRowAsync(address, method, ct);

    private async Task<bool> MarkRowAsync(string address, UnsubscribeMethod method, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Senders.Where(s => s.Address == address).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.UnsubscribedAt, now)
            .SetProperty(s => s.UnsubscribeMethod, method), ct) > 0;
    }

    /// <summary>
    /// The option from the newest stored, not-deleted message with a usable <c>List-Unsubscribe</c>. An https target
    /// needs the message's live <c>List-Unsubscribe-Post</c> (not stored), so it is read from Gmail; a message Gmail no
    /// longer knows is skipped, up to <see cref="MaxMessageTries"/> messages.
    /// </summary>
    private async Task<(UnsubscribeOption? Option, string? MessageId)> ResolveAsync(string address, CancellationToken ct)
    {
        var candidates = await db.Messages.AsNoTracking()
            .Where(m => m.FromAddress == address && !m.DeletedInGmail && m.ListUnsubscribe != null && m.ListUnsubscribe != "")
            .OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.ListUnsubscribe })
            .Take(MaxMessageTries)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            var uris = ListUnsubscribeParser.Parse(candidate.ListUnsubscribe);
            if (uris.Count == 0)
            {
                continue;
            }

            if (!uris.Any(u => u.Scheme == Uri.UriSchemeHttps))
            {
                return (ListUnsubscribeParser.Resolve(uris, null), candidate.Id);
            }

            var live = await gmail.GetMessagesMetadataAsync([candidate.Id], ct);
            if (live.Count == 0)
            {
                continue;
            }

            return (ListUnsubscribeParser.Resolve(uris, live[0].ListUnsubscribePost), candidate.Id);
        }

        return (null, null);
    }
}
