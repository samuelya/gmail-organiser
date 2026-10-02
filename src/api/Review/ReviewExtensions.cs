using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;

namespace GmailOrganiser.Review;

public static class ReviewExtensions
{
    /// <summary>Registers review, decisions, the label cache and the apply job. Needs <c>AddGmail</c>, <c>AddAnalysis</c>, <c>AddFetch</c> and <c>AddJobs</c>.</summary>
    public static IServiceCollection AddReview(this IServiceCollection services)
    {
        services.AddSingleton<LabelCatalog>();
        services.AddScoped<DecisionRecorder>();
        services.AddScoped<ReviewQuery>();
        services.AddScoped<ReviewService>();
        services.AddScoped<LabelResolver>();
        services.AddScoped<ApplyService>();

        // The apply job mutates Gmail: refused while the local data belongs to another account.
        services.AddKeyedScoped<IJobRunGuard, FetchAccountJobGuard>(ApplyActionsJob.JobType);
        services.AddKeyedScoped<IJobHandler, ApplyActionsJob>(ApplyActionsJob.JobType);
        return services;
    }
}
