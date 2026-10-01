using GmailOrganiser.Gmail.Fake;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail;

public static class GmailExtensions
{
    /// <summary>
    /// Registers <see cref="IGmailClient"/> and <see cref="ITokenStore"/>: the Google implementations, or the in-memory
    /// fakes when <see cref="GmailOptions.UseFake"/> is set. The choice is made at resolution time so hosts can override it.
    /// Needs <c>AddAppDatabase</c> and <c>AddSettings</c>.
    /// </summary>
    public static IServiceCollection AddGmail(this IServiceCollection services)
    {
        services.AddOptions<GmailOptions>()
            .BindConfiguration(GmailOptions.SectionName)
            .Configure<IConfiguration>((o, configuration) =>
            {
                var value = configuration[GmailOptions.FakeEnvironmentKey];
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                o.UseFake = bool.TryParse(value.Trim(), out var useFake)
                    ? useFake
                    : throw new InvalidOperationException($"{GmailOptions.FakeEnvironmentKey} must be 'true' or 'false'.");
            })
            .ValidateOnStart();

        services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<FakeTokenStore>();
        services.AddScoped<GoogleGmailClient>();
        services.AddScoped<TokenStore>();

        services.AddScoped<IGmailClient>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeGmailClient>()
            : sp.GetRequiredService<GoogleGmailClient>());
        services.AddScoped<ITokenStore>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeTokenStore>()
            : sp.GetRequiredService<TokenStore>());
        return services;
    }

    private static bool UseFake(IServiceProvider sp) => sp.GetRequiredService<IOptions<GmailOptions>>().Value.UseFake;
}
