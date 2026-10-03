using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailOrganiser.CleanUp.Unsubscribe;

/// <summary>How a sender can be (or was) unsubscribed from (DESIGN §6.4, epic #24 Q-X1).</summary>
[JsonConverter(typeof(UnsubscribeMethodJsonConverter))]
public enum UnsubscribeMethod
{
    /// <summary>RFC 8058: the api POSTs to the https URL.</summary>
    OneClick,

    /// <summary>An http(s) page the user opens in their browser.</summary>
    Link,

    /// <summary>A <c>mailto:</c> address the user's mail client sends; the api never sends mail.</summary>
    Mailto,
}

/// <summary>Serialises <see cref="UnsubscribeMethod"/> as <c>one_click | link | mailto</c>.</summary>
public sealed class UnsubscribeMethodJsonConverter()
    : JsonStringEnumConverter<UnsubscribeMethod>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);

/// <param name="Method">Null when none of the sender's newest messages offers a usable <c>List-Unsubscribe</c>.</param>
/// <param name="Url">The https URL (one-click or link), the http link, or the <c>mailto:</c> URL unchanged.</param>
/// <param name="MessageId">The message whose headers gave <paramref name="Method"/>.</param>
/// <param name="UnsubscribedVia">How the sender was last unsubscribed from, if ever.</param>
public sealed record UnsubscribeInfoDto(
    UnsubscribeMethod? Method,
    string? Url,
    string? MessageId,
    DateTimeOffset? UnsubscribedAt,
    UnsubscribeMethod? UnsubscribedVia);

/// <param name="Status"><c>done</c> on a 2xx answer; <c>failed</c> otherwise (any 3xx, error, timeout or rejected target).</param>
/// <param name="HttpStatus">The status the sender's server answered with; null when no answer arrived.</param>
public sealed record UnsubscribeResultDto(string Status, int? HttpStatus)
{
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <param name="Method"><c>link</c> or <c>mailto</c>: what the user opened by hand.</param>
public sealed record MarkUnsubscribedRequest(UnsubscribeMethod Method);
