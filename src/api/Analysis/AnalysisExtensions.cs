using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;

namespace GmailOrganiser.Analysis;

public static class AnalysisExtensions
{
    /// <summary>Registers grouping, the run service and the run job. Needs <c>AddGmail</c>, <c>AddLlm</c>, <c>AddJobs</c> and <c>AddFetch</c>; the short-circuit comes from <c>AddMemory</c>.</summary>
    public static IServiceCollection AddAnalysis(this IServiceCollection services)
    {
        services.AddAnalysisGrouping();
        services.AddScoped<AnalysisRunService>();
        // The run reads Gmail bodies: refused while the local data belongs to another account.
        services.AddKeyedScoped<IJobRunGuard, FetchAccountJobGuard>(AnalysisRunJob.JobType);
        services.AddKeyedScoped<IJobHandler, AnalysisRunJob>(AnalysisRunJob.JobType);
        return services;
    }
}
