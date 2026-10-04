using GmailOrganiser.Jobs;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Rules.Review;

namespace GmailOrganiser.Rules;

public static class RulesExtensions
{
    /// <summary>Registers the filter snapshot, filter service, proposals, label plans, filter reviews and their summaries. Needs <c>AddGmail</c>, <c>AddLlm</c> and <c>AddReview</c> (label catalog, resolver, sender patterns).</summary>
    public static IServiceCollection AddRules(this IServiceCollection services)
    {
        services.AddScoped<FilterSnapshot>();
        services.AddScoped<FilterService>();
        services.AddScoped<FilterProposalQuery>();
        services.AddScoped<LabelPlanService>();
        services.AddScoped<FilterReviewService>();
        services.AddScoped<FilterReviewSummariser>();

        // Its account guard comes with FetchJobTypes.ReadsGmail; a cancel is refused while a merge chunk is pending.
        services.AddKeyedScoped<IJobCancelHook, LabelPlanApplyJob>(LabelPlanApplyJob.JobType);
        services.AddKeyedScoped<IJobHandler, LabelPlanApplyJob>(LabelPlanApplyJob.JobType);
        return services;
    }
}
