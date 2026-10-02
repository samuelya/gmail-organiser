using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;

namespace GmailOrganiser.Fetch;

public static class FetchExtensions
{
    /// <summary>Registers the fetch pipeline and the fetch job handlers. Needs <c>AddGmail</c>, <c>AddSettings</c> and <c>AddJobs</c>.</summary>
    public static IServiceCollection AddFetch(this IServiceCollection services)
    {
        services.AddScoped<MessageUpserter>();
        services.AddScoped<SenderStatsUpdater>();
        services.AddScoped<MessageFetchPipeline>();
        services.AddKeyedScoped<IJobHandler, MailboxFetchJob>(MailboxFetchJob.JobType);
        return services;
    }
}
