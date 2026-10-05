using GmailOrganiser.Jobs;

namespace GmailOrganiser.CleanUp;

public static class CleanUpExtensions
{
    /// <summary>Registers the Clean-up list and its trash/unmark job, and the retention sweep. Needs <c>AddReview</c>, <c>AddPolicies</c>, <c>AddFetch</c> and <c>AddJobs</c>.</summary>
    public static IServiceCollection AddCleanUp(this IServiceCollection services)
    {
        services.AddScoped<CleanUpQuery>();
        services.AddScoped<CleanUpService>();

        // Its account guard comes with FetchJobTypes.ReadsGmail; a cancel is refused while a chunk is pending.
        services.AddKeyedScoped<IJobCancelHook, CleanUpActionsJob>(CleanUpActionsJob.JobType);
        services.AddKeyedScoped<IJobHandler, CleanUpActionsJob>(CleanUpActionsJob.JobType);

        // The retention sweep (#368): same guard and cancel rule; the scheduler queues it daily while retention is on.
        services.AddScoped<RetentionService>();
        services.AddKeyedScoped<IJobCancelHook, RetentionSweepJob>(RetentionSweepJob.JobType);
        services.AddKeyedScoped<IJobHandler, RetentionSweepJob>(RetentionSweepJob.JobType);
        services.AddHostedService<RetentionScheduler>();
        return services;
    }
}
