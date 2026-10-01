namespace GmailOrganiser.Common;

public static class SecurityExtensions
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName))
            .Validate(
                o => o.AllowedOrigins.All(v => SecurityOptions.TryNormaliseOrigin(v, out _)),
                "Security:AllowedOrigins must contain http(s) origins of the form scheme://host[:port], without a path, query or user info.")
            .ValidateOnStart();
        return services;
    }

    public static IApplicationBuilder UseApiRequestGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiRequestGuardMiddleware>();
}
