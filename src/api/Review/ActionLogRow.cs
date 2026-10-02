using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>
/// One Gmail mutation of one message (<c>action_log</c>), with the label IDs before and after so it can be undone.
/// <see cref="SuggestionId"/> has no foreign key: re-analyse deletes suggestions, the log stays.
/// </summary>
public sealed class ActionLogRow
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public string MessageId { get; set; } = "";
    public Guid? SuggestionId { get; set; }

    /// <summary>Label names added, for display.</summary>
    public string[] LabelsAdded { get; set; } = [];

    /// <summary>Label names removed, for display.</summary>
    public string[] LabelsRemoved { get; set; } = [];
    public string[] LabelIdsBefore { get; set; } = [];
    public string[] LabelIdsAfter { get; set; } = [];
    public string? Note { get; set; }
    public Guid? UndoneByBatchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActionLogRow>(e =>
        {
            e.ToTable("action_log");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.MessageId).IsRequired();
            e.Property(r => r.LabelsAdded).IsRequired();
            e.Property(r => r.LabelsRemoved).IsRequired();
            e.Property(r => r.LabelIdsBefore).IsRequired();
            e.Property(r => r.LabelIdsAfter).IsRequired();
            e.HasOne<ActionBatchRow>().WithMany().HasForeignKey(r => r.BatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => r.BatchId);
            e.HasIndex(r => r.MessageId);
        });
    }
}
