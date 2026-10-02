using System.Threading.Channels;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Memory;

/// <summary>Wakes the decision embedding in the background; never waits for it.</summary>
public interface IDecisionEmbeddingQueue
{
    void Notify();
}

/// <summary>A single pending wake-up: notifications while one is pending merge into it.</summary>
public sealed class DecisionEmbeddingQueue : IDecisionEmbeddingQueue
{
    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => signal.Writer.TryWrite(true);

    /// <summary>Returns when notified or after <paramref name="timeout"/>, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var notified = signal.Reader.WaitToReadAsync(wait.Token).AsTask();
        await Task.WhenAny(notified, Task.Delay(timeout, time, wait.Token));
        await wait.CancelAsync();
        ct.ThrowIfCancellationRequested();
        signal.Reader.TryRead(out _);
    }
}

/// <summary>
/// Embeds decisions that have no vector, off the review's request path: after each notification and every
/// <see cref="RetryInterval"/>, so rows left behind by an unavailable model are retried later. Best effort: a failure
/// leaves the rows for the next pass.
/// </summary>
public sealed partial class DecisionEmbeddingService(
    IServiceScopeFactory scopes, DecisionEmbeddingQueue queue, TimeProvider time, ILogger<DecisionEmbeddingService> logger)
    : BackgroundService
{
    public const int BatchSize = 100;
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>Embeds pending decisions, newest first, in batches until none is left or the model fails; returns how many.</summary>
    public async Task<int> EmbedPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var memory = scope.ServiceProvider.GetRequiredService<IDecisionMemory>();
        var embedded = 0;
        while (true)
        {
            var rows = await db.Decisions
                .Where(d => d.Embedding == null)
                .OrderByDescending(d => d.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(ct);
            await memory.EmbedAsync(rows, ct);
            var done = rows.Count(r => r.Embedding is not null);
            if (done == 0)
            {
                return embedded;
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            embedded += done;
            if (rows.Count < BatchSize)
            {
                return embedded;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EmbedPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(logger, ex);
            }

            try
            {
                await queue.WaitAsync(RetryInterval, time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Decision embedding pass failed; retrying on the next notification or interval")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
