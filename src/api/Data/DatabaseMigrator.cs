using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GmailOrganiser.Data;

/// <summary>
/// Applies pending EF Core migrations during host start-up. It is registered before the web server
/// starts, so the API never serves requests against an unmigrated database. Transient connection
/// errors (database still starting) are retried; any other failure aborts start-up.
/// </summary>
public sealed partial class DatabaseMigrator(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                LogMigrated(logger);
                return;
            }
            catch (NpgsqlException ex) when (ex.IsTransient && attempt <= settings.MigrationRetries)
            {
                LogRetrying(logger, attempt, settings.MigrationRetries, settings.MigrationRetryDelay, ex.Message);
                await Task.Delay(settings.MigrationRetryDelay, timeProvider, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
                throw;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations are up to date")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed; the API will not start")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Database not reachable (attempt {Attempt} of {Retries} retries), retrying in {Delay}: {Reason}")]
    private static partial void LogRetrying(ILogger logger, int attempt, int retries, TimeSpan delay, string reason);
}
