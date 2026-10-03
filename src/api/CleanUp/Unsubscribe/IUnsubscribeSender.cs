namespace GmailOrganiser.CleanUp.Unsubscribe;

/// <summary>Sends the RFC 8058 one-click POST. Tests use <c>FakeUnsubscribeSender</c>; they never reach the network.</summary>
public interface IUnsubscribeSender
{
    /// <summary>
    /// POSTs <c>List-Unsubscribe=One-Click</c> to <paramref name="url"/> if <see cref="UnsubscribeTargetGuard"/> allows
    /// it. Never throws for a refused target, a network error, a timeout or a non-2xx answer: those are a failed result.
    /// </summary>
    Task<UnsubscribeSendResult> SendOneClickAsync(Uri url, CancellationToken ct);
}

/// <param name="Done">True on a 2xx answer.</param>
/// <param name="HttpStatus">The answer's status code; null when the target was refused or no answer arrived.</param>
public sealed record UnsubscribeSendResult(bool Done, int? HttpStatus);
