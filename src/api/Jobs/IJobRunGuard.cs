namespace GmailOrganiser.Jobs;

/// <summary>
/// Vetoes runs of one job type. Register with <c>services.AddKeyedScoped&lt;IJobRunGuard, TGuard&gt;(jobType)</c>;
/// <see cref="JobRunner"/> asks it before the handler runs and <see cref="JobContext"/> again at every checkpoint and
/// wherever the handler calls <see cref="JobContext.EnsureMayWriteAsync"/>, so a condition that changes mid-run (e.g. a
/// reconnect to another account) stops the job before its next write.
/// </summary>
public interface IJobRunGuard
{
    /// <summary>Null when the job may run; otherwise the reason, recorded as the job's error.</summary>
    Task<string?> RefuseReasonAsync(CancellationToken ct);
}

/// <summary>Ends the run as <c>failed</c> with <see cref="Exception.Message"/> as the job's error; thrown by guards and handlers.</summary>
public sealed class JobRefusedException(string reason) : Exception(reason);

/// <summary>
/// Lets a job type refuse, or follow up on, a cancel of its job while the job is not running (a running handler sees
/// the cancel at its next checkpoint). Register with <c>services.AddKeyedScoped&lt;IJobCancelHook, THook&gt;(jobType)</c>.
/// </summary>
public interface IJobCancelHook
{
    /// <summary>False while the stored <paramref name="cursor"/> has work that only a resume can finish.</summary>
    bool AllowsCancel(string? cursor);

    /// <summary>Runs after a queued, paused or failed job was cancelled.</summary>
    Task CancelledAsync(string? cursor, CancellationToken ct);
}

/// <summary>
/// Repairs a feature's own rows once <see cref="JobRunner.RecoverAsync"/> has re-queued the interrupted jobs at start-up
/// (e.g. an analysis run whose job is gone). Register with <c>services.AddScoped&lt;IJobStartupRecovery, T&gt;()</c>; a
/// failure is logged and never keeps the runner from claiming jobs.
/// </summary>
public interface IJobStartupRecovery
{
    Task RecoverAsync(CancellationToken ct);
}
