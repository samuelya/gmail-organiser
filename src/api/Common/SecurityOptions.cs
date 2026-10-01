namespace GmailOrganiser.Common;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Origins allowed to send state-changing requests to <c>/api/**</c>, e.g. <c>http://localhost:4200</c>.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Normalises a configured origin to the form a browser sends in the <c>Origin</c> header:
    /// <c>scheme://host[:non-default-port]</c>, lower case. Rejects anything that is not an absolute
    /// http(s) URI or that carries user info, a path, a query or a fragment, since such a value could
    /// never match a browser origin.
    /// </summary>
    public static bool TryNormaliseOrigin(string? value, out string origin)
    {
        origin = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return true;
    }
}
