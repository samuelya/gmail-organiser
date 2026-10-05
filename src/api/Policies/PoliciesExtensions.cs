namespace GmailOrganiser.Policies;

/// <summary>Sender-policy settings that are not user-facing, bound from the <c>Policies</c> section.</summary>
public sealed class PolicyOptions
{
    public const string SectionName = "Policies";

    /// <summary>
    /// Whole words (or phrases) in a subject or snippet that mark mail as transactional (<see cref="TransactionalGuard"/>).
    /// The default list is in <c>appsettings.json</c>; extend or localise it with <c>Policies__TransactionalKeywords__&lt;n&gt;</c>.
    /// </summary>
    public List<string> TransactionalKeywords { get; set; } = [];
}

public static class PoliciesExtensions
{
    /// <summary>Registers the transactional guard, the policy matcher, the sender profile builder and the policy review.</summary>
    public static IServiceCollection AddPolicies(this IServiceCollection services)
    {
        services.AddOptions<PolicyOptions>().BindConfiguration(PolicyOptions.SectionName);
        services.AddSingleton<TransactionalGuard>();
        services.AddSingleton<PolicyMatcher>();
        services.AddScoped<SenderProfileBuilder>();
        services.AddScoped<PolicyQuery>();
        services.AddScoped<PolicyService>();
        return services;
    }
}
