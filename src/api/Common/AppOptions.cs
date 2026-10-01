namespace GmailOrganiser.Common;

/// <summary>
/// Where the user reaches the app, bound from <c>App:*</c>; <c>APP_BASE_URL</c> (from <c>.env</c>) overrides
/// <see cref="BaseUrl"/>. The OAuth redirect URI and the post-connect redirect are built from it.
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";
    public const string BaseUrlEnvironmentKey = "APP_BASE_URL";

    /// <summary>Absolute http(s) origin the browser uses, e.g. the <c>ng serve</c> or the compose <c>web</c> address.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary><see cref="BaseUrl"/> without a trailing slash.</summary>
    public string NormalisedBaseUrl => BaseUrl.Trim().TrimEnd('/');

    public static bool IsValidBaseUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}

public static class AppOptionsExtensions
{
    public static IServiceCollection AddAppOptions(this IServiceCollection services)
    {
        services.AddOptions<AppOptions>()
            .BindConfiguration(AppOptions.SectionName)
            .Configure<IConfiguration>((o, configuration) =>
            {
                var value = configuration[AppOptions.BaseUrlEnvironmentKey];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    o.BaseUrl = value.Trim();
                }
            })
            .Validate(o => AppOptions.IsValidBaseUrl(o.BaseUrl), $"{AppOptions.BaseUrlEnvironmentKey} (App:BaseUrl) must be an absolute http(s) URL without a path, e.g. scheme://host:port.")
            .ValidateOnStart();
        return services;
    }
}
