namespace GmailOrganiser.Jobs;

/// <summary>
/// Runs one job type. Register with <c>services.AddKeyedScoped&lt;IJobHandler, THandler&gt;(type)</c>.
/// A handler reads its cursor, does one idempotent unit of work at a time and calls
/// <see cref="JobContext.CheckpointAsync{T}"/> after each; it returns as soon as the signal is not
/// <see cref="JobSignal.Continue"/>. The job may restart from the last checkpoint at any time.
/// </summary>
public interface IJobHandler
{
    string Type { get; }

    Task RunAsync(JobContext ctx, CancellationToken ct);
}

/// <summary>Receives every job status change and checkpoint (SignalR in #55).</summary>
public interface IJobProgressPublisher
{
    Task JobChangedAsync(JobDto job, CancellationToken ct);
}

internal sealed class NoOpJobProgressPublisher : IJobProgressPublisher
{
    public Task JobChangedAsync(JobDto job, CancellationToken ct) => Task.CompletedTask;
}
