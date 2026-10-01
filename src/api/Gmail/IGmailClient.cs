namespace GmailOrganiser.Gmail;

/// <summary>
/// The Gmail operations the app uses, shaped by its consumers (not a wrapper of the Google SDK).
/// Later milestones add methods as they need them.
/// </summary>
public interface IGmailClient
{
    /// <summary>The connected mailbox's address, message count and current history ID.</summary>
    /// <exception cref="GmailNotConnectedException">No usable refresh token or Google client is configured.</exception>
    Task<GmailProfile> GetProfileAsync(CancellationToken ct);
}

/// <param name="HistoryId">Gmail's history ID as a decimal string (it is an unsigned 64-bit number).</param>
public sealed record GmailProfile(string EmailAddress, long MessagesTotal, string HistoryId);

/// <summary>The app is not connected to Gmail (or the connection was revoked); the user must reconnect.</summary>
public sealed class GmailNotConnectedException(string message, Exception? inner = null) : Exception(message, inner);
