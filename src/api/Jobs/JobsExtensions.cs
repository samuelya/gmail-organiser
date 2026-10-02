using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GmailOrganiser.Jobs;

public static class JobsExtensions
{
    /// <summary>
    /// Registers the job service, the runner and the SignalR progress publisher. Handlers register themselves with
    /// <c>AddKeyedScoped&lt;IJobHandler, THandler&gt;(type)</c>. Call after <c>AddAppDatabase()</c> so
    /// migrations run before the runner starts.
    /// </summary>
    public static IServiceCollection AddJobs(this IServiceCollection services)
    {
        services.AddOptions<JobsOptions>()
            .BindConfiguration(JobsOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSignalR();
        services.TryAddSingleton<IJobProgressPublisher, SignalRJobProgressPublisher>();
        services.AddScoped<JobNotifier>();
        services.AddScoped<IJobService, JobService>();
        services.AddHostedService<JobRunner>();
        return services;
    }
}
