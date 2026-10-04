using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

/// <summary>
/// A compare run's answer for one suggestion (<c>suggestion_alternatives</c>), stored next to it and never applied by
/// the run; at most one per suggestion, from the latest-created compare run (#249). Removed with its suggestion; outlives its run.
/// Columns mean what they mean on <see cref="SuggestionRow"/>.
/// </summary>
public sealed class SuggestionAlternativeRow
{
    public Guid Id { get; set; }
    public Guid SuggestionId { get; set; }
    public string MessageId { get; set; } = "";
    public Guid? RunId { get; set; }
    public SuggestionSource Source { get; set; }
    public string? GroupKey { get; set; }
    public string TopicLabel { get; set; } = "";
    public bool IsNewLabel { get; set; }
    public string? DocumentTypeLabel { get; set; }
    public bool DocumentTypeIsNew { get; set; }
    public string[] ReplaceLabelIds { get; set; } = [];
    public string[] ReplaceLabels { get; set; } = [];
    public bool NeedsAction { get; set; }
    public bool ToBeDeleted { get; set; }
    public bool UnsubscribeSuggested { get; set; }

    /// <summary>0–1, checked like <see cref="SuggestionRow.Confidence"/>.</summary>
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
    public string? FilterCriteria { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The alternative a compare run produced for <paramref name="suggestionId"/> from a row it built like a suggestion.</summary>
    public static SuggestionAlternativeRow From(SuggestionRow row, Guid suggestionId, Guid runId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        SuggestionId = suggestionId,
        MessageId = row.MessageId,
        RunId = runId,
        Source = row.Source,
        GroupKey = row.GroupKey,
        TopicLabel = row.TopicLabel,
        IsNewLabel = row.IsNewLabel,
        DocumentTypeLabel = row.DocumentTypeLabel,
        DocumentTypeIsNew = row.DocumentTypeIsNew,
        ReplaceLabelIds = row.ReplaceLabelIds,
        ReplaceLabels = row.ReplaceLabels,
        NeedsAction = row.NeedsAction,
        ToBeDeleted = row.ToBeDeleted,
        UnsubscribeSuggested = row.UnsubscribeSuggested,
        Confidence = row.Confidence,
        Reason = row.Reason,
        FilterCriteria = row.FilterCriteria,
        Model = row.Model,
        PromptVersion = row.PromptVersion,
        CreatedAt = now,
    };

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SuggestionAlternativeRow>(e =>
        {
            e.ToTable("suggestion_alternatives", t => t.HasCheckConstraint("ck_suggestion_alternatives_confidence", "confidence BETWEEN 0 AND 1"));
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.MessageId).IsRequired();
            e.Property(r => r.Source).IsRequired().HasConversion(new SnakeCaseEnumConverter<SuggestionSource>());
            e.Property(r => r.TopicLabel).IsRequired();
            e.Property(r => r.Reason).IsRequired();
            e.Property(r => r.ReplaceLabelIds).IsRequired().HasDefaultValueSql("'{}'");
            e.Property(r => r.ReplaceLabels).IsRequired().HasDefaultValueSql("'{}'");
            e.Property(r => r.FilterCriteria).HasColumnType("jsonb");
            e.HasOne<SuggestionRow>().WithMany().HasForeignKey(r => r.SuggestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AnalysisRunRow>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(r => r.SuggestionId).IsUnique();
            e.HasIndex(r => r.RunId);
        });
    }
}
