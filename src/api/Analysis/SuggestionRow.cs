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
/// old row first. Removed with its message; outlives its run. <see cref="Status"/> is authoritative;
/// <see cref="MessageRow.AnalysisStatus"/> is a denormalised copy for inbox filtering, written only through
/// <see cref="SetStatus"/> (or reset to <see cref="AnalysisStatus.NotAnalysed"/> when the suggestion is deleted).
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

    /// <summary>
    /// 0–1. The <c>ck_suggestions_confidence</c> check rejects NaN and out-of-range values and fails the whole
    /// <c>SaveChanges</c>, so callers clamp to [0, 1] and treat NaN as invalid LLM output before saving.
    /// </summary>
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>Sender-level Gmail filter suggestion as JSON.</summary>
    public string? FilterCriteria { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public SuggestionStatus Status { get; private set; }

    /// <summary>The user changed the suggestion before deciding.</summary>
    public bool Edited { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>
    /// Sets <see cref="Status"/> and mirrors it onto <paramref name="message"/>; the only writer of both.
    /// <see cref="DecidedAt"/> is the latest approve or reject (a flip shows when it happened); applying keeps it, and
    /// the full history is in <c>decisions</c>.
    /// </summary>
    public void SetStatus(SuggestionStatus status, MessageRow message, DateTimeOffset at)
    {
        if (message.Id != MessageId)
        {
            throw new ArgumentException($"Message {message.Id} does not belong to suggestion {Id}.", nameof(message));
        }

        Status = status;
        DecidedAt = status switch
        {
            SuggestionStatus.Pending => null,
            SuggestionStatus.Applied => DecidedAt ?? at,
            _ => at,
        };
        message.AnalysisStatus = ToAnalysisStatus(status);
        message.UpdatedAt = at;
    }

    public static AnalysisStatus ToAnalysisStatus(SuggestionStatus status) => status switch
    {
        SuggestionStatus.Pending => AnalysisStatus.Analysed,
        SuggestionStatus.Approved => AnalysisStatus.Approved,
        SuggestionStatus.Rejected => AnalysisStatus.Rejected,
        SuggestionStatus.Applied => AnalysisStatus.Applied,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

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
