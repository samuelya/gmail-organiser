using System.ComponentModel.DataAnnotations;

namespace GmailOrganiser.Data;

/// <summary>Start-up migration settings, bound from the <c>Database</c> section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Extra attempts when the database is not reachable yet (transient errors only).</summary>
    [Range(0, 60)]
    public int MigrationRetries { get; set; } = 5;

    /// <summary>Delay between attempts.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan MigrationRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
}
