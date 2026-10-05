using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;

namespace GmailOrganiser.Fetch;

public static class FetchExtensions
{
    /// <summary>Registers the fetch pipeline, the fetch job handlers and the sender maintenance jobs. Needs <c>AddGmail</c>, <c>AddSettings</c> and <c>AddJobs</c>.</summary>
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
        return services;
    }
}
