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

    /// <summary>The second label under the document-type parent; null when none (memory and sender-pattern rows for now).</summary>
    public string? DocumentTypeLabel { get; set; }

    /// <summary><see cref="DocumentTypeLabel"/> was not in the label tree at analysis time (case-insensitive).</summary>
    public bool DocumentTypeIsNew { get; set; }
    public bool NeedsAction { get; set; }
    public bool ToBeDeleted { get; set; }
    public bool UnsubscribeSuggested { get; set; }

    /// <summary>
    /// Ids of the message's personal labels the suggestion replaces (labelled phase, DESIGN §6.3); apply removes those
    /// the message still carries, so a label renamed in Gmail is still removed. Empty for memory and sender-pattern
    /// suggestions. Written through <see cref="SetReplaced"/> only.
    /// </summary>
    public string[] ReplaceLabelIds { get; set; } = [];

    /// <summary>The names of <see cref="ReplaceLabelIds"/> when stored, same order; display only.</summary>
    public string[] ReplaceLabels { get; set; } = [];

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
    /// <c>created_at</c> of the latest compare run that wrote an alternative for this suggestion (#309); kept when the
    /// alternative is discarded or accepted, so an older compare run that resumes later does not write over it.
    /// </summary>
    public DateTimeOffset? CompareRunCreatedAt { get; set; }

    /// <summary>Sets the replaced labels (id, name); true when the set of ids changed.</summary>
    public bool SetReplaced(IReadOnlyList<(string Id, string Name)> labels)
    {
        var changed = !labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal).SetEquals(ReplaceLabelIds);
        ReplaceLabelIds = [.. labels.Select(l => l.Id)];
        ReplaceLabels = [.. labels.Select(l => l.Name)];
        return changed;
    }

    /// <summary>The replaced labels as (id, name): the current Gmail name when <paramref name="names"/> knows it, else the stored one.</summary>
    public IReadOnlyList<(string Id, string Name)> Replaced(IReadOnlyDictionary<string, string>? names) =>
        Replaced(ReplaceLabelIds, ReplaceLabels, names);

    /// <summary>
    /// Pairs <paramref name="ids"/> with their names: the current Gmail name when <paramref name="names"/> knows it, else
    /// the index-aligned <paramref name="stored"/> one, else the id.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> Replaced(
        string[] ids, string[] stored, IReadOnlyDictionary<string, string>? names) =>
        [.. ids.Select((id, i) => (id, names?.GetValueOrDefault(id) ?? (i < stored.Length ? stored[i] : id)))];

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
            e.Property(r => r.ReplaceLabelIds).IsRequired().HasDefaultValueSql("'{}'");
            e.Property(r => r.ReplaceLabels).IsRequired().HasDefaultValueSql("'{}'");
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
