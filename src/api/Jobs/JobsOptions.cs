using System.ComponentModel.DataAnnotations;

namespace GmailOrganiser.Jobs;

/// <summary>Background job runner settings, bound from the <c>Jobs</c> section.</summary>
public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>How often the runner looks for queued jobs.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:10:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
