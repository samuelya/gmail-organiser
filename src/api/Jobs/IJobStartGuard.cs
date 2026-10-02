namespace GmailOrganiser.Jobs;

/// <summary>
/// Vetoes runs of a queue's jobs. Register with <c>services.AddKeyedScoped&lt;IJobStartGuard, TGuard&gt;(queue)</c>;
/// <see cref="JobRunner"/> asks it after claiming a job and before its handler runs.
/// </summary>
public interface IJobStartGuard
{
    /// <summary>Null when the job may run; otherwise the reason, recorded as the job's error.</summary>
    Task<string?> RefuseReasonAsync(CancellationToken ct);
}
