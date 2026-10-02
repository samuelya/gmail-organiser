using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Jobs;

/// <summary>
/// Pushes job changes to <see cref="JobsHub"/> without ever waiting on client I/O. Every job write increments
/// <see cref="JobDto.Version"/> in its <c>UPDATE</c> and every published DTO is read from the row after that write,
/// so a DTO's state and version always match: a DTO not newer than the newest one accepted is dropped, on every path. Status changes go out at
/// once; checkpoints of a job whose status is unchanged are throttled to one per
/// <see cref="JobsOptions.ProgressInterval"/>, and the latest parked checkpoint is sent when the interval elapses
/// unless a newer event superseded it. Sends run on one pump per job, in order, so a slow client delays only that
/// job's events, never the caller; while it is stalled the oldest queued events are dropped beyond
/// <see cref="MaxQueued"/>, so the latest state always goes out.
/// </summary>
internal sealed partial class SignalRJobProgressPublisher(
    IHubContext<JobsHub> hub,
    IOptions<JobsOptions> options,
    TimeProvider time,
    ILogger<SignalRJobProgressPublisher> logger) : IJobProgressPublisher, IDisposable
{
    /// <summary>How long a finished job's last state is kept to reject late, older publishes.</summary>
    private static readonly TimeSpan TombstoneTtl = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for one broadcast, so a stalled client can't hold a job's pump for good.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Events kept per job while a send is in flight; checkpoints are already throttled, so this rarely fills.</summary>
    private const int MaxQueued = 32;

    private static readonly HashSet<string> FinishedStatuses =
        JobRow.Finished.Select(JobRow.FormatStatus).ToHashSet(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, JobStream> _streams = new();
    private readonly TimeSpan _interval = options.Value.ProgressInterval;
    private readonly CancellationTokenSource _stopping = new();

    public Task JobChangedAsync(JobDto job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var finished = FinishedStatuses.Contains(job.Status);
        var stream = _streams.GetOrAdd(job.Id, static _ => new JobStream());
        lock (stream.Sync)
        {
            if (IsStale(stream.Latest, job))
            {
                return Task.CompletedTask;
            }

            stream.Latest = job;
            if (!finished && stream.LastStatus == job.Status && now - stream.LastSentAt < _interval)
            {
                stream.Parked = job;
                if (stream.Timer is null)
                {
                    var state = new TimerState(stream, ++stream.TimerGeneration);
                    stream.Timer = time.CreateTimer(
                        s => Flush((TimerState)s!), state, stream.LastSentAt + _interval - now, Timeout.InfiniteTimeSpan);
                }

                return Task.CompletedTask;
            }

            Enqueue(stream, job, finished, now);
        }

        if (finished)
        {
            SweepTombstones(now);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        foreach (var stream in _streams.Values)
        {
            lock (stream.Sync)
            {
                CancelTimer(stream);
            }
        }

        _stopping.Dispose();
    }

    /// <summary>Not newer than the newest accepted DTO; an equal version is the same state, already accepted.</summary>
    private static bool IsStale(JobDto? latest, JobDto job) => latest is not null && job.Version <= latest.Version;

    private void Flush(TimerState state)
    {
        var stream = state.Stream;
        lock (stream.Sync)
        {
            // A later event already replaced or cancelled this timer; its parked DTO (if any) belongs to the new one.
            if (stream.TimerGeneration != state.Generation || stream.Parked is not { } parked)
            {
                return;
            }

            Enqueue(stream, parked, finished: false, time.GetUtcNow());
        }
    }

    /// <summary>Hands <paramref name="job"/> to the job's pump; caller holds <see cref="JobStream.Sync"/>.</summary>
    private void Enqueue(JobStream stream, JobDto job, bool finished, DateTimeOffset now)
    {
        stream.Parked = null;
        CancelTimer(stream);
        stream.LastStatus = job.Status;
        stream.LastSentAt = now;
        stream.FinishedAt = finished ? now : null;
        if (stream.Outbox.Count == MaxQueued)
        {
            stream.Outbox.Dequeue();
        }

        stream.Outbox.Enqueue(job);
        if (!stream.Pumping)
        {
            stream.Pumping = true;
            _ = Task.Run(() => PumpAsync(stream));
        }
    }

    private async Task PumpAsync(JobStream stream)
    {
        while (true)
        {
            JobDto next;
            lock (stream.Sync)
            {
                if (_stopping.IsCancellationRequested || !stream.Outbox.TryDequeue(out next!))
                {
                    stream.Pumping = false;
                    return;
                }
            }

            try
            {
                using var timeout = new CancellationTokenSource(SendTimeout, time);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _stopping.Token);
                await hub.Clients.All.SendAsync(JobsHub.ChangedEvent, next, cts.Token);
            }
            catch (Exception ex)
            {
                LogSendFailed(logger, next.Id, ex);
            }
        }
    }

    private void SweepTombstones(DateTimeOffset now)
    {
        foreach (var (id, stream) in _streams)
        {
            lock (stream.Sync)
            {
                if (stream.FinishedAt is { } at && now - at >= TombstoneTtl && !stream.Pumping)
                {
                    _streams.TryRemove(new KeyValuePair<Guid, JobStream>(id, stream));
                }
            }
        }
    }

    private static void CancelTimer(JobStream stream)
    {
        stream.TimerGeneration++;
        stream.Timer?.Dispose();
        stream.Timer = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sending progress for job {JobId} failed")]
    private static partial void LogSendFailed(ILogger logger, Guid jobId, Exception exception);

    private sealed record TimerState(JobStream Stream, long Generation);

    /// <summary>Per-job send state; every field is read and written only while holding <see cref="Sync"/>.</summary>
    private sealed class JobStream
    {
        public Lock Sync { get; } = new();
        public JobDto? Latest { get; set; }
        public string? LastStatus { get; set; }
        public DateTimeOffset LastSentAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public JobDto? Parked { get; set; }
        public ITimer? Timer { get; set; }
        public long TimerGeneration { get; set; }
        public Queue<JobDto> Outbox { get; } = new();
        public bool Pumping { get; set; }
    }
}
