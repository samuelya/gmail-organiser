using System.Net.Sockets;
using System.Text.Json;
using OllamaSharp.Models.Exceptions;

namespace GmailOrganiser.Llm;

/// <summary>Turns HTTP failures talking to Ollama into messages a user can act on.</summary>
public static class OllamaErrors
{
    public static bool IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException or OllamaException
            or NotSupportedException or InvalidOperationException;

    public static string Describe(Exception ex, Uri url, TimeSpan timeout)
    {
        var target = url.GetLeftPart(UriPartial.Authority);
        var message = Find<SocketException>(ex)?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => $"Connection refused by {target}. Is Ollama running?",
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"Host not found: {url.Host}.",
            _ => null,
        };

        message ??= ex switch
        {
            TaskCanceledException or TimeoutException => $"No answer from {target} within {timeout.TotalSeconds:0} s.",
            HttpRequestException { StatusCode: { } status } =>
                $"{target} answered HTTP {(int)status}; it does not look like an Ollama server.",
            JsonException or NotSupportedException or OllamaException or InvalidOperationException =>
                $"{target} answered, but not like an Ollama server.",
            _ => $"Could not reach {target}.",
        };

        return message + LocalhostHint(url);
    }

    // From the IDE the API runs on the host itself, where the Docker-only host name does not resolve.
    private static string LocalhostHint(Uri url) =>
        url.IsLoopback
            ? " If the API runs in Docker, use the host's Docker address instead of localhost."
            : $" If the API runs from the IDE (not in Docker), use {url.Scheme}://localhost:{url.Port} instead.";

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is T match)
            {
                return match;
            }
        }

        return null;
    }
}
