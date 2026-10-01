namespace GmailOrganiser.Common;

public static class SecurityExtensions
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .Validate(o => o.AllowedOrigins.All(IsAbsoluteHttpOrigin), "Security:AllowedOrigins must contain absolute http(s) origins.")
            .ValidateOnStart();
        return services;
    }

    public static IApplicationBuilder UseApiRequestGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiRequestGuardMiddleware>();

    private static bool IsAbsoluteHttpOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
