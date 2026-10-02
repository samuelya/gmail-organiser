using System.Text.Json;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

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

    public static readonly JobStatus[] Active = [JobStatus.Queued, JobStatus.Running, JobStatus.Paused];
    public static readonly JobStatus[] Finished = [JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled];

    internal static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public JobDto ToDto() => new(
        Id, Type, Queue, FormatStatus(Status),
        Progress is null ? null : JsonSerializer.Deserialize<JobProgress>(Progress, Json),
        Error, CreatedAt, StartedAt, UpdatedAt, FinishedAt);

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
            e.HasIndex(r => new { r.Queue, r.Status, r.CreatedAt });
        });
    }
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
    DateTimeOffset? FinishedAt);
