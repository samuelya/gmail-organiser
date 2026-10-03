using GmailOrganiser.Jobs;

namespace GmailOrganiser.CleanUp;

public static class CleanUpExtensions
{
    /// <summary>Registers the Clean-up list and its trash/unmark job. Needs <c>AddReview</c>, <c>AddFetch</c> and <c>AddJobs</c>.</summary>
    public static IServiceCollection AddCleanUp(this IServiceCollection services)
    {
        services.AddScoped<CleanUpQuery>();
        services.AddScoped<CleanUpService>();

        // Its account guard comes with FetchJobTypes.ReadsGmail; a cancel is refused while a chunk is pending.
        services.AddKeyedScoped<IJobCancelHook, CleanUpActionsJob>(CleanUpActionsJob.JobType);
        services.AddKeyedScoped<IJobHandler, CleanUpActionsJob>(CleanUpActionsJob.JobType);
        return services;
    }
}
