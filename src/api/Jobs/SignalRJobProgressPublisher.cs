using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Jobs;

/// <summary>
/// Pushes job changes to <see cref="JobsHub"/>. Status changes go out at once; checkpoints of a job whose
/// status is unchanged are throttled to one per <see cref="JobsOptions.ProgressInterval"/>, and the latest
/// parked checkpoint is sent when the interval elapses unless a newer status event superseded it.
/// </summary>
internal sealed partial class SignalRJobProgressPublisher(
    IHubContext<JobsHub> hub,
    IOptions<JobsOptions> options,
    TimeProvider time,
    ILogger<SignalRJobProgressPublisher> logger) : IJobProgressPublisher, IDisposable
{
    private readonly ConcurrentDictionary<Guid, JobStream> _streams = new();
    private readonly TimeSpan _interval = options.Value.ProgressInterval;

    public async Task JobChangedAsync(JobDto job, CancellationToken ct)
    {
        var stream = _streams.GetOrAdd(job.Id, _ => new JobStream());
        await stream.Gate.WaitAsync(ct);
        try
        {
            var finished = IsFinished(job.Status);
            var now = time.GetUtcNow();
            var statusChanged = stream.LastStatus != job.Status;
            if (!statusChanged && !finished && now - stream.LastSentAt < _interval)
            {
                stream.Pending = job;
                stream.Timer ??= time.CreateTimer(_ => _ = FlushAsync(job.Id, stream), null, stream.LastSentAt + _interval - now, Timeout.InfiniteTimeSpan);
                return;
            }

            stream.Pending = null;
            DisposeTimer(stream);
            if (finished)
            {
                stream.Finished = true;
                _streams.TryRemove(new KeyValuePair<Guid, JobStream>(job.Id, stream));
            }

            await SendAsync(stream, job, now, ct);
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var stream in _streams.Values)
        {
            DisposeTimer(stream);
        }
    }

    private async Task FlushAsync(Guid jobId, JobStream stream)
    {
        try
        {
            await stream.Gate.WaitAsync();
            try
            {
                DisposeTimer(stream);
                if (stream.Finished || stream.Pending is not { } pending)
                {
                    return;
                }

                stream.Pending = null;
                await SendAsync(stream, pending, time.GetUtcNow(), CancellationToken.None);
            }
            finally
            {
                stream.Gate.Release();
            }
        }
        catch (Exception ex)
        {
            LogFlushFailed(logger, jobId, ex);
        }
    }

    private async Task SendAsync(JobStream stream, JobDto job, DateTimeOffset now, CancellationToken ct)
    {
        stream.LastStatus = job.Status;
        stream.LastSentAt = now;
        await hub.Clients.All.SendAsync(JobsHub.ChangedEvent, job, ct);
    }

    private static void DisposeTimer(JobStream stream)
    {
        stream.Timer?.Dispose();
        stream.Timer = null;
    }

    private static bool IsFinished(string status) =>
        JobRow.Finished.Any(s => JobRow.FormatStatus(s) == status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending throttled progress for job {JobId} failed")]
    private static partial void LogFlushFailed(ILogger logger, Guid jobId, Exception exception);

    /// <summary>Per-job send state; every field is read and written only while holding <see cref="Gate"/>.</summary>
    private sealed class JobStream
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? LastStatus { get; set; }
        public DateTimeOffset LastSentAt { get; set; }
        public JobDto? Pending { get; set; }
        public ITimer? Timer { get; set; }
        public bool Finished { get; set; }
    }
}
