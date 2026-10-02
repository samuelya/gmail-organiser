using GmailOrganiser.Jobs;

namespace GmailOrganiser.Review;

public static class ReviewExtensions
{
    /// <summary>Registers review, decisions, the label cache, the apply and undo jobs and History. Needs <c>AddGmail</c>, <c>AddAnalysis</c>, <c>AddFetch</c> and <c>AddJobs</c>.</summary>
    public static IServiceCollection AddReview(this IServiceCollection services)
    {
        services.AddSingleton<LabelCatalog>();
        services.AddScoped<DecisionRecorder>();
        services.AddScoped<ReviewQuery>();
        services.AddScoped<ReviewService>();
        services.AddScoped<LabelResolver>();
        services.AddScoped<ApplyService>();
        services.AddScoped<SenderPatternService>();
        services.AddScoped<HistoryQuery>();

        // Their account guard comes with FetchJobTypes.ReadsGmail; a cancel is refused while a chunk is pending.
        services.AddKeyedScoped<IJobCancelHook, ApplyActionsJob>(ApplyActionsJob.JobType);
        services.AddKeyedScoped<IJobHandler, ApplyActionsJob>(ApplyActionsJob.JobType);
        services.AddKeyedScoped<IJobCancelHook, UndoActionsJob>(UndoActionsJob.JobType);
        services.AddKeyedScoped<IJobHandler, UndoActionsJob>(UndoActionsJob.JobType);
        return services;
    }
}
