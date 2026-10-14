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

    /// <summary>
    /// The outcome's additional labels (#582), canonical like <see cref="SuggestionRow.ExtraLabels"/>; accept adds them to the
    /// suggestion's own. Empty for alternatives stored before the column existed.
    /// </summary>
    public string[] ExtraLabels { get; set; } = [];

    /// <summary>0–1, checked like <see cref="SuggestionRow.Confidence"/>.</summary>
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
    public string? FilterCriteria { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The suggestion whose outcome was copied here (#562); provenance only, null once the source is gone.</summary>
    public Guid? CopiedFromSuggestionId { get; set; }

    /// <summary>A copy of another suggestion's outcome (#562), not a compare run's answer; stays true once the source is gone.</summary>
    public bool Copied { get; set; }

    /// <summary>
    /// A copy's source run (#574): the run whose outcome was copied, null for a compare run's answer. Not a run answer, so
    /// run-scoped queries don't see it; accept gives it to the suggestion, so its decision is decided like the source's.
    /// </summary>
    public Guid? OutcomeRunId { get; set; }

    /// <summary>The copied outcome was edited by hand (<see cref="SuggestionRow.Edited"/>); accept carries it. False for a compare run's answer.</summary>
    public bool Edited { get; set; }

    /// <summary>The replaced labels as (id, name), like <see cref="SuggestionRow.Replaced(IReadOnlyDictionary{string, string}?)"/>.</summary>
    public IReadOnlyList<(string Id, string Name)> Replaced(IReadOnlyDictionary<string, string>? names) =>
        SuggestionRow.Replaced(ReplaceLabelIds, ReplaceLabels, names);

    /// <summary>The alternative a compare run produced for <paramref name="suggestionId"/> from a row it built like a suggestion.</summary>
    public static SuggestionAlternativeRow From(SuggestionRow row, Guid suggestionId, Guid runId, DateTimeOffset now) =>
        Build(row, suggestionId, row.MessageId, row.GroupKey, row.Replaced(null), now, alternative => alternative.RunId = runId);

    /// <summary>
    /// A copy of <paramref name="source"/>'s current outcome for <paramref name="target"/> (#562), with only those of the
    /// source's replaced labels (by id) that the target's message carries (<paramref name="targetLabelIds"/>). It has no run,
    /// so run-scoped compare and recovery never take it for the source run's answer, and that run's comment isn't shown on it;
    /// the source's run is kept as <see cref="OutcomeRunId"/>.
    /// </summary>
    public static SuggestionAlternativeRow CopyOf(
        SuggestionRow source, SuggestionRow target, IReadOnlyCollection<string> targetLabelIds, DateTimeOffset now) =>
        Build(source, target.Id, target.MessageId, target.GroupKey,
            [.. source.Replaced(null).Where(l => targetLabelIds.Contains(l.Id, StringComparer.Ordinal))], now,
            alternative =>
            {
                alternative.CopiedFromSuggestionId = source.Id;
                alternative.Copied = true;
                alternative.Edited = source.Edited;
                alternative.OutcomeRunId = source.RunId;
            });

    /// <summary>The outcome of <paramref name="outcome"/> for one suggestion; the single list of outcome fields.</summary>
    private static SuggestionAlternativeRow Build(
        SuggestionRow outcome, Guid suggestionId, string messageId, string? groupKey, IReadOnlyList<(string Id, string Name)> replaced,
        DateTimeOffset now, Action<SuggestionAlternativeRow> provenance)
    {
        var alternative = new SuggestionAlternativeRow
        {
            Id = Guid.CreateVersion7(now),
            SuggestionId = suggestionId,
            MessageId = messageId,
            Source = outcome.Source,
            GroupKey = groupKey,
            TopicLabel = outcome.TopicLabel,
            IsNewLabel = outcome.IsNewLabel,
            DocumentTypeLabel = outcome.DocumentTypeLabel,
            DocumentTypeIsNew = outcome.DocumentTypeIsNew,
            MailType = outcome.MailType,
            ProposedNewLabel = outcome.ProposedNewLabel,
            ReplaceLabelIds = [.. replaced.Select(l => l.Id)],
            ReplaceLabels = [.. replaced.Select(l => l.Name)],
            NeedsAction = outcome.NeedsAction,
            ToBeDeleted = outcome.ToBeDeleted,
            UnsubscribeSuggested = outcome.UnsubscribeSuggested,
            ExtraLabels = [.. outcome.ExtraLabels],
            Confidence = outcome.Confidence,
            Reason = outcome.Reason,
            FilterCriteria = outcome.FilterCriteria,
            Model = outcome.Model,
            PromptVersion = outcome.PromptVersion,
            CreatedAt = now,
        };
        provenance(alternative);
        return alternative;
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
            e.Property(r => r.ExtraLabels).IsRequired().HasDefaultValueSql("'{}'");
            e.Property(r => r.FilterCriteria).HasColumnType("jsonb");
            e.HasOne<SuggestionRow>().WithMany().HasForeignKey(r => r.SuggestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AnalysisRunRow>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(r => r.SuggestionId).IsUnique();
            e.HasIndex(r => r.RunId);
            e.HasOne<SuggestionRow>().WithMany().HasForeignKey(r => r.CopiedFromSuggestionId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(r => r.CopiedFromSuggestionId);
            e.HasOne<AnalysisRunRow>().WithMany().HasForeignKey(r => r.OutcomeRunId).OnDelete(DeleteBehavior.SetNull);
            e.Property(r => r.Copied).HasDefaultValue(false);
            e.Property(r => r.Edited).HasDefaultValue(false);
        });
    }
}
