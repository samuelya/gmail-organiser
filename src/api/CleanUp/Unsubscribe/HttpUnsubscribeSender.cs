using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace GmailOrganiser.CleanUp.Unsubscribe;

/// <summary>The named <see cref="HttpClient"/> for one-click unsubscribe and the handler that enforces the SSRF guard.</summary>
public static class UnsubscribeHttp
{
    public const string ClientName = "unsubscribe";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The product name and version only.</summary>
    public static readonly ProductInfoHeaderValue UserAgent = new(
        "GmailOrganiser", typeof(UnsubscribeHttp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>
    /// No redirects, cookies, credentials or proxy (a proxy would connect for us, past the guard). The connect callback
    /// resolves the host itself, checks every address with <see cref="UnsubscribeTargetGuard.Validate"/> and connects
    /// only to those addresses, so the check and the connection use the same resolution.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        PreAuthenticate = false,
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var url = context.InitialRequestMessage.RequestUri ?? throw new UnsubscribeTargetRefusedException(UnsubscribeTargetVerdict.NotHttps);
        var verdict = UnsubscribeTargetGuard.ValidateUrl(url);
        IPAddress[] addresses = [];
        if (verdict == UnsubscribeTargetVerdict.Allowed)
        {
            addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            verdict = UnsubscribeTargetGuard.Validate(url, addresses);
        }

        if (verdict != UnsubscribeTargetVerdict.Allowed)
        {
            throw new UnsubscribeTargetRefusedException(verdict);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>The guard refused the target; no connection was made.</summary>
public sealed class UnsubscribeTargetRefusedException(UnsubscribeTargetVerdict verdict)
    : IOException($"Unsubscribe target refused: {verdict}.")
{
    public UnsubscribeTargetVerdict Verdict { get; } = verdict;
}

/// <summary>RFC 8058 one-click POST over <see cref="UnsubscribeHttp.ClientName"/>. Logs the host only, never the URL.</summary>
public sealed class HttpUnsubscribeSender(IHttpClientFactory httpClientFactory, ILogger<HttpUnsubscribeSender> logger) : IUnsubscribeSender
{
    public async Task<UnsubscribeSendResult> SendOneClickAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        var verdict = UnsubscribeTargetGuard.ValidateUrl(url);
        if (verdict != UnsubscribeTargetVerdict.Allowed)
        {
            logger.LogWarning("One-click unsubscribe to {Host} refused: {Verdict}", url.IsAbsoluteUri ? url.Host : "", verdict);
            return new UnsubscribeSendResult(false, null);
        }

        // Body "List-Unsubscribe=One-Click" as application/x-www-form-urlencoded (RFC 8058 §3.2).
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]),
        };
        request.Headers.UserAgent.Add(UnsubscribeHttp.UserAgent);

        try
        {
            using var client = httpClientFactory.CreateClient(UnsubscribeHttp.ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var done = response.IsSuccessStatusCode;
            logger.LogInformation("One-click unsubscribe to {Host} answered {Status}", url.Host, status);
            return new UnsubscribeSendResult(done, status);
        }
        catch (HttpRequestException ex) when ((ex.InnerException ?? ex) is UnsubscribeTargetRefusedException refused)
        {
            // SocketsHttpHandler wraps what the connect callback throws.
            logger.LogWarning("One-click unsubscribe to {Host} refused: {Verdict}", url.Host, refused.Verdict);
            return new UnsubscribeSendResult(false, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Timeout or network error; the message may carry the URL, so only the type is logged.
            logger.LogWarning("One-click unsubscribe to {Host} failed ({Error})", url.Host, ex.GetType().Name);
            return new UnsubscribeSendResult(false, null);
        }
    }
}
