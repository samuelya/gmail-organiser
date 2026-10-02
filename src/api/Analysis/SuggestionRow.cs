using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

/// <summary>Where a suggestion came from; derived and memory suggestions count as analysed.</summary>
public enum SuggestionSource
{
    Llm,
    Derived,
    Memory,
    SenderPattern,
}

public enum SuggestionStatus
{
    Pending,
    Approved,
    Rejected,
    Applied,
}

/// <summary>
/// The current suggestion for one message (<c>suggestions</c>); at most one per message, so re-analyse deletes the
/// old row first. Removed with its message; outlives its run.
/// </summary>
public sealed class SuggestionRow
{
    public Guid Id { get; set; }
    public string MessageId { get; set; } = "";
    public Guid? RunId { get; set; }
    public string SenderAddress { get; set; } = "";

    /// <summary>Messages sharing a key were covered by one representative answer.</summary>
    public string? GroupKey { get; set; }
    public SuggestionSource Source { get; set; }
    public string TopicLabel { get; set; } = "";
    public bool IsNewLabel { get; set; }
    public bool NeedsAction { get; set; }
    public bool ToBeDeleted { get; set; }
    public bool UnsubscribeSuggested { get; set; }

    /// <summary>0–1.</summary>
    public float Confidence { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>Sender-level Gmail filter suggestion as JSON.</summary>
    public string? FilterCriteria { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public SuggestionStatus Status { get; set; }

    /// <summary>The user changed the suggestion before deciding.</summary>
    public bool Edited { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SuggestionRow>(e =>
        {
            e.ToTable("suggestions", t => t.HasCheckConstraint("ck_suggestions_confidence", "confidence BETWEEN 0 AND 1"));
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.MessageId).IsRequired();
            e.Property(r => r.SenderAddress).IsRequired();
            e.Property(r => r.Source).IsRequired().HasConversion(new SnakeCaseEnumConverter<SuggestionSource>());
            e.Property(r => r.TopicLabel).IsRequired();
            e.Property(r => r.Reason).IsRequired();
            e.Property(r => r.FilterCriteria).HasColumnType("jsonb");
            e.Property(r => r.Status).IsRequired().HasConversion(new SnakeCaseEnumConverter<SuggestionStatus>());
            e.HasOne<MessageRow>().WithMany().HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AnalysisRunRow>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(r => r.MessageId).IsUnique();
            e.HasIndex(r => new { r.SenderAddress, r.Status });
            e.HasIndex(r => r.RunId);
            e.HasIndex(r => new { r.Status, r.Confidence });
        });
    }
}
