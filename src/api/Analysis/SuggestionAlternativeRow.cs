using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

/// <summary>
/// A compare run's answer for one suggestion (<c>suggestion_alternatives</c>), stored next to it and never applied by
/// the run; at most one per suggestion, from the latest-created compare run (#249) or copy of another suggestion's outcome
/// (#562). Removed with its suggestion; outlives its run and its copy source.
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
    public MailType? MailType { get; set; }
    public string? ProposedNewLabel { get; set; }
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

    /// <summary>The suggestion whose outcome was copied here (#562); null for a compare run's answer or once the source is gone.</summary>
    public Guid? CopiedFromSuggestionId { get; set; }

    /// <summary>The replaced labels as (id, name), like <see cref="SuggestionRow.Replaced(IReadOnlyDictionary{string, string}?)"/>.</summary>
    public IReadOnlyList<(string Id, string Name)> Replaced(IReadOnlyDictionary<string, string>? names) =>
        SuggestionRow.Replaced(ReplaceLabelIds, ReplaceLabels, names);

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
        MailType = row.MailType,
        ProposedNewLabel = row.ProposedNewLabel,
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

    /// <summary>
    /// A copy of <paramref name="source"/>'s current outcome for <paramref name="target"/> (#562), with only those of the
    /// source's replaced labels (by id) that <paramref name="message"/>, the target's message, carries.
    /// </summary>
    public static SuggestionAlternativeRow CopyOf(SuggestionRow source, SuggestionRow target, MessageRow message, DateTimeOffset now)
    {
        var replaced = source.Replaced(null).Where(l => message.LabelIds.Contains(l.Id, StringComparer.Ordinal)).ToList();
        return new()
        {
            Id = Guid.CreateVersion7(now),
            SuggestionId = target.Id,
            MessageId = target.MessageId,
            RunId = source.RunId,
            Source = source.Source,
            GroupKey = target.GroupKey,
            TopicLabel = source.TopicLabel,
            IsNewLabel = source.IsNewLabel,
            DocumentTypeLabel = source.DocumentTypeLabel,
            DocumentTypeIsNew = source.DocumentTypeIsNew,
            MailType = source.MailType,
            ProposedNewLabel = source.ProposedNewLabel,
            ReplaceLabelIds = [.. replaced.Select(l => l.Id)],
            ReplaceLabels = [.. replaced.Select(l => l.Name)],
            NeedsAction = source.NeedsAction,
            ToBeDeleted = source.ToBeDeleted,
            UnsubscribeSuggested = source.UnsubscribeSuggested,
            Confidence = source.Confidence,
            Reason = source.Reason,
            FilterCriteria = source.FilterCriteria,
            Model = source.Model,
            PromptVersion = source.PromptVersion,
            CreatedAt = now,
            CopiedFromSuggestionId = source.Id,
        };
    }

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
            e.Property(r => r.MailType).HasConversion(new SnakeCaseEnumConverter<MailType>());
            e.Property(r => r.ReplaceLabelIds).IsRequired().HasDefaultValueSql("'{}'");
            e.Property(r => r.ReplaceLabels).IsRequired().HasDefaultValueSql("'{}'");
            e.Property(r => r.FilterCriteria).HasColumnType("jsonb");
            e.HasOne<SuggestionRow>().WithMany().HasForeignKey(r => r.SuggestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AnalysisRunRow>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(r => r.SuggestionId).IsUnique();
            e.HasIndex(r => r.RunId);
            e.HasOne<SuggestionRow>().WithMany().HasForeignKey(r => r.CopiedFromSuggestionId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(r => r.CopiedFromSuggestionId);
        });
    }
}
