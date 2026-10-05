using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;

namespace GmailOrganiser.Fetch;

public static class FetchExtensions
{
    /// <summary>Registers the fetch pipeline, the fetch job handlers and the sender maintenance and Stage-0 jobs. Needs <c>AddGmail</c>, <c>AddSettings</c>, <c>AddJobs</c> and <c>AddReview</c>.</summary>
    public static IServiceCollection AddFetch(this IServiceCollection services)
    {
        services.AddSingleton<MailboxTotalsReader>();
        services.AddScoped<MessageUpserter>();
        services.AddScoped<SenderStatsUpdater>();
        services.AddScoped<SenderAllowlist>();
        services.AddScoped<MessageFetchPipeline>();
        services.AddScoped<IAccountGuard, AccountGuard>();
        services.AddScoped<LocalAccountClaim>();
        foreach (var type in FetchJobTypes.ReadsGmail)
        {
            services.AddKeyedScoped<IJobRunGuard, FetchAccountJobGuard>(type);
        }

        services.AddKeyedScoped<IJobHandler, MailboxFetchJob>(MailboxFetchJob.JobType);
        services.AddKeyedScoped<IJobHandler, SenderFetchJob>(SenderFetchJob.JobType);
        services.AddKeyedScoped<IJobHandler, IncrementalFetchJob>(IncrementalFetchJob.JobType);
        services.AddKeyedScoped<IJobHandler, LabelResyncJob>(LabelResyncJob.JobType);
        services.AddKeyedScoped<IJobHandler, CanonicalBackfillJob>(CanonicalBackfillJob.JobType);
        services.AddKeyedScoped<IJobHandler, SenderStatsRebuildJob>(SenderStatsRebuildJob.JobType);

        // Stage-0 actions (#349) use the Review label catalog. The archive job's account guard comes with
        // FetchJobTypes.ReadsGmail; a cancel is refused while a chunk is pending.
        services.AddScoped<Stage0Service>();
        services.AddKeyedScoped<IJobCancelHook, SenderArchiveJob>(SenderArchiveJob.JobType);
        services.AddKeyedScoped<IJobHandler, SenderArchiveJob>(SenderArchiveJob.JobType);
        return services;
    }
}
