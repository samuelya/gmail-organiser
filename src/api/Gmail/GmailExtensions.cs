using GmailOrganiser.Common;
using GmailOrganiser.Gmail.Auth;
using GmailOrganiser.Gmail.Fake;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail;

public static class GmailExtensions
{
    /// <summary>
    /// Registers <see cref="IGmailClient"/> and <see cref="ITokenStore"/>: the Google implementations, or the in-memory
    /// fakes when <see cref="GmailOptions.UseFake"/> is set. The choice is made at resolution time so hosts can override it.
    /// Also registers the OAuth connect flow. Needs <c>AddAppDatabase</c> and <c>AddSettings</c>.
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
            .Validate(
                o => o.BatchSize is >= 1 and <= GmailOptions.MaxBatchSize,
                $"{GmailOptions.SectionName}:BatchSize must be between 1 and {GmailOptions.MaxBatchSize}.")
            .Validate(
                o => o.QuotaUnitsPerSecond is >= GmailQuotaLimiter.MessageCallUnits and <= GmailOptions.GmailUnitsPerSecondLimit,
                $"{GmailOptions.SectionName}:QuotaUnitsPerSecond must be between {GmailQuotaLimiter.MessageCallUnits} and {GmailOptions.GmailUnitsPerSecondLimit}.")
            .Validate(
                o => o.MaxRetryAttempts is >= 1 and <= GmailOptions.MaxRetryAttemptsLimit,
                $"{GmailOptions.SectionName}:MaxRetryAttempts must be between 1 and {GmailOptions.MaxRetryAttemptsLimit}.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new GmailRetryPolicy(sp.GetRequiredService<IOptions<GmailOptions>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<GmailQuotaLimiter>();
        services.AddSingleton(sp => new FakeGmailClient(
            sp.GetRequiredService<FakeTokenStore>(),
            FakeGmailClient.Seed(sp.GetRequiredService<TimeProvider>().GetUtcNow()),
            sp.GetRequiredService<GmailRetryPolicy>()));
        services.AddSingleton<FakeTokenStore>();
        services.AddScoped<GoogleGmailClient>();
        services.AddScoped<TokenStore>();

        services.AddScoped<IGmailClient>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeGmailClient>()
            : sp.GetRequiredService<GoogleGmailClient>());
        services.AddScoped<ITokenStore>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeTokenStore>()
            : sp.GetRequiredService<TokenStore>());

        // OAuth connect flow (Gmail/Auth).
        services.AddAppOptions();
        services.AddOptions<GoogleOAuthOptions>()
            .BindConfiguration(GoogleOAuthOptions.SectionName)
            .Validate(o => o.StateLifetime > TimeSpan.Zero, $"{GoogleOAuthOptions.SectionName}:StateLifetime must be a positive time span, e.g. 00:10:00.")
            .Validate(
                o => o.RevokeTimeout > TimeSpan.Zero && o.RevokeTimeout <= GoogleOAuthOptions.MaxRevokeTimeout,
                $"{GoogleOAuthOptions.SectionName}:RevokeTimeout must be a positive time span of at most {GoogleOAuthOptions.MaxRevokeTimeout}, e.g. 00:00:10.")
            .ValidateOnStart();
        services.AddHttpClient<GoogleOAuthClient>(GoogleOAuthClient.HttpClientName);
        services.AddSingleton<FakeGoogleOAuthClient>();
        services.AddScoped<IGoogleOAuthClient>(sp => UseFake(sp)
            ? sp.GetRequiredService<FakeGoogleOAuthClient>()
            : sp.GetRequiredService<GoogleOAuthClient>());
        services.AddSingleton<OAuthStateCookie>();
        services.AddScoped<GmailConnector>();
        return services;
    }

    private static bool UseFake(IServiceProvider sp) => sp.GetRequiredService<IOptions<GmailOptions>>().Value.UseFake;
}
