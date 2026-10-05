namespace GmailOrganiser.Dashboard;

/// <summary>
/// Every <see cref="CheckInterval"/>, takes a triage snapshot if one is due (<see cref="MetricsSnapshotter.TakeIfDueAsync"/>):
/// hourly, and after a completed fetch, incremental, analysis or policy-apply job. Stateless, so a failed pass is logged
/// and simply retried on the next tick.
/// </summary>
public sealed partial class MetricsScheduler(IServiceScopeFactory scopes, TimeProvider time, ILogger<MetricsScheduler> logger)
    : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, time);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<MetricsSnapshotter>().TakeIfDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogPassFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Triage metrics snapshot failed; retrying on the next tick")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
