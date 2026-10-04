namespace GmailOrganiser.Rules;

public static class RulesExtensions
{
    /// <summary>Registers the filter snapshot, filter service and proposals. Needs <c>AddGmail</c> and <c>AddReview</c> (label catalog, resolver, sender patterns).</summary>
    public static IServiceCollection AddRules(this IServiceCollection services)
    {
        services.AddScoped<FilterSnapshot>();
        services.AddScoped<FilterService>();
        services.AddScoped<FilterProposalQuery>();
        return services;
    }
}
