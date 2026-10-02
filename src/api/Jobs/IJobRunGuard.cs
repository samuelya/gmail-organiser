namespace GmailOrganiser.Jobs;

/// <summary>
/// Vetoes runs of one job type. Register with <c>services.AddKeyedScoped&lt;IJobRunGuard, TGuard&gt;(jobType)</c>;
/// <see cref="JobRunner"/> asks it before the handler runs and <see cref="JobContext"/> again at every checkpoint, so a
/// condition that changes mid-run (e.g. a reconnect to another account) stops the job at its next checkpoint.
/// </summary>
public interface IJobRunGuard
{
    /// <summary>Null when the job may run; otherwise the reason, recorded as the job's error.</summary>
    Task<string?> RefuseReasonAsync(CancellationToken ct);
}

/// <summary>Ends the run as <c>failed</c> with <see cref="Exception.Message"/> as the job's error; thrown by guards and handlers.</summary>
public sealed class JobRefusedException(string reason) : Exception(reason);
