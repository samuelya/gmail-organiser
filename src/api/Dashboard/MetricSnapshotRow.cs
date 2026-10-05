using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Dashboard;

/// <summary>
/// The triage metrics at one moment (<c>metric_snapshots</c>), taken by <see cref="MetricsSnapshotter"/>. Counts are of live
/// messages (not deleted in Gmail); the LLM totals are running sums over every analysis run.
/// </summary>
public sealed class MetricSnapshotRow
{
    public Guid Id { get; set; }
    public DateTimeOffset TakenAt { get; set; }
    public int MessagesTotal { get; set; }
    public int InboxCount { get; set; }
    public int InboxUnreadCount { get; set; }

    /// <summary>Messages an approved non-mixed policy covers, or that have a policy suggestion.</summary>
    public int CoveredByPolicy { get; set; }

    /// <summary>Messages an active filter's <c>from</c> or <c>list:</c> term matches.</summary>
    public int CoveredByFilter { get; set; }

    public int Analysed { get; set; }
    public int Applied { get; set; }
    public int ToBeDeleted { get; set; }
    public long LlmMillisecondsTotal { get; set; }
    public long PromptTokensTotal { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetricSnapshotRow>(e =>
        {
            e.ToTable("metric_snapshots");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.HasIndex(r => r.TakenAt);
        });
    }
}
