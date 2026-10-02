using System.Text.Json;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace GmailOrganiser.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Queue names; one job runs at a time per queue.</summary>
public static class JobQueues
{
    public const string Fetch = "fetch";
}

/// <summary>A background job (table <c>jobs</c>). Cursor and progress are handler-defined JSON.</summary>
public sealed class JobRow
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Queue { get; set; } = "";
    public JobStatus Status { get; set; }
    public string? Cursor { get; set; }
    public string? Progress { get; set; }
    public string? Error { get; set; }
    public bool PauseRequested { get; set; }
    public bool CancelRequested { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Per-job version: every write sets <c>version = version + 1</c> in its own <c>UPDATE</c>. The row lock
    /// orders those statements, so a higher version is always the later state (unlike <see cref="UpdatedAt"/>,
    /// which is taken from the app clock before the write).
    /// </summary>
    public long Version { get; set; }

    public static readonly JobStatus[] Active = [JobStatus.Queued, JobStatus.Running, JobStatus.Paused];
    public static readonly JobStatus[] Finished = [JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled];

    internal static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <summary>Unique partial index: at most one queued, running or paused job per type.</summary>
    internal const string ActiveTypeIndex = "ux_jobs_type_active";

    public JobDto ToDto() => new(
        Id, Type, Queue, FormatStatus(Status),
        Progress is null ? null : JsonSerializer.Deserialize<JobProgress>(Progress, Json),
        Error, CreatedAt, StartedAt, UpdatedAt, FinishedAt, Version);

    public static string FormatStatus(JobStatus status) => SnakeCaseEnumConverter<JobStatus>.ToDb(status);

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobRow>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Type).IsRequired();
            e.Property(r => r.Queue).IsRequired();
            e.Property(r => r.Status).IsRequired().HasConversion(new SnakeCaseEnumConverter<JobStatus>());
            e.Property(r => r.Cursor).HasColumnType("jsonb");
            e.Property(r => r.Progress).HasColumnType("jsonb");
            e.Property(r => r.PauseRequested).HasDefaultValue(false);
            e.Property(r => r.CancelRequested).HasDefaultValue(false);
            e.Property(r => r.Version).HasDefaultValue(0L);
            e.HasIndex(r => new { r.Queue, r.Status, r.CreatedAt });
            e.HasIndex(r => r.Type)
                .IsUnique()
                .HasDatabaseName(ActiveTypeIndex)
                .HasFilter("status IN ('queued', 'running', 'paused')");
        });
    }
}

internal static class JobRowUpdates
{
    /// <summary>Every job write ends with this: sets <c>updated_at</c> and increments <c>version</c> in the same <c>UPDATE</c>.</summary>
    public static UpdateSettersBuilder<JobRow> Touch(this UpdateSettersBuilder<JobRow> s, DateTimeOffset now) => s
        .SetProperty(j => j.UpdatedAt, now)
        .SetProperty(j => j.Version, j => j.Version + 1);
}

public sealed record JobProgress(long Done, long? Total, string? Message);

public sealed record JobDto(
    Guid Id,
    string Type,
    string Queue,
    string Status,
    JobProgress? Progress,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt,
    long Version);
