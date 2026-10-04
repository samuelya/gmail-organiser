using GmailOrganiser.Rules.Labels;

namespace GmailOrganiser.Rules;

public static class RulesExtensions
{
    /// <summary>Registers the filter snapshot and the label plans. Needs <c>AddGmail</c> and <c>AddReview</c> (the label catalog).</summary>
    public static IServiceCollection AddRules(this IServiceCollection services)
    {
        services.AddScoped<FilterSnapshot>();
        services.AddScoped<LabelPlanService>();
        return services;
    }
}
