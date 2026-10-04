using GmailOrganiser.Rules.Labels;

namespace GmailOrganiser.Rules;

public static class RulesExtensions
{
    /// <summary>Registers the filter snapshot, filter service, proposals and label plans. Needs <c>AddGmail</c> and <c>AddReview</c> (label catalog, resolver, sender patterns).</summary>
    public static IServiceCollection AddRules(this IServiceCollection services)
    {
        services.AddScoped<FilterSnapshot>();
        services.AddScoped<FilterService>();
        services.AddScoped<FilterProposalQuery>();
        services.AddScoped<LabelPlanService>();
        return services;
    }
}
