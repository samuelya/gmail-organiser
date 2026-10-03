using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

public enum ActionKind
{
    Apply,
    Undo,
    ApplyRest,
    AutoArchive,

    /// <summary>Clean-up Delete: moved to Trash (#179).</summary>
    Trash,

    /// <summary>Clean-up: the delete label removed (#179).</summary>
    Unmark,
}

/// <summary>One History entry (<c>action_batches</c>): the header of a set of <see cref="ActionLogRow"/>s undone together.</summary>
public sealed class ActionBatchRow
{
    public Guid Id { get; set; }
    public ActionKind Kind { get; set; }
    public string Description { get; set; } = "";
    public int MessageCount { get; set; }

    /// <summary>For an <see cref="ActionKind.Undo"/> batch, the batch it reverted.</summary>
    public Guid? UndoOf { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
    public Guid? JobId { get; set; }

    /// <summary>Gmail label ids this batch created, recorded right after each create, so undo can tell them apart.</summary>
    public string[] CreatedLabelIds { get; set; } = [];

    /// <summary>Failed re-sends of a pending <see cref="ActionKind.AutoArchive"/> batch, or of an apply batch's pending chunk.</summary>
    public int SendFailures { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActionBatchRow>(e =>
        {
            e.ToTable("action_batches");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Kind).IsRequired().HasConversion(new SnakeCaseEnumConverter<ActionKind>());
            e.Property(r => r.Description).IsRequired();
            e.Property(r => r.CreatedLabelIds).IsRequired().HasDefaultValueSql("'{}'");
            e.Property(r => r.SendFailures).HasDefaultValue(0);
            e.HasIndex(r => r.CreatedAt);
        });
    }
}
