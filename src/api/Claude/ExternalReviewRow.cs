using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Rules.Review;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Claude;

/// <summary>What a Claude review item points at.</summary>
public enum ExternalReviewTarget
{
    Suggestion,
    Group,
    LabelPlan,
    FilterFinding,
}

public enum ExternalReviewStatus
{
    Queued,
    Running,
    Reviewed,
    Unavailable,
    Cancelled,
}

public enum ReviewVerdict
{
    Agree,
    Alternative,
    NeedsHuman,
}

/// <summary>What the user did with a reviewed item.</summary>
public enum ExternalReviewResolution
{
    None,
    AcceptedClaude,
    Dismissed,
}

/// <summary>
/// One Claude review item (<c>external_reviews</c>): a local suggestion, a review group (sender + group key), a label
/// plan or a filter finding (sender address empty), and
/// Claude's verdict once submitted. Open items are <c>Queued|Running</c>, or <c>Reviewed</c> without a resolution.
/// </summary>
public sealed class ExternalReviewRow
{
    /// <summary>At most one open item per suggestion.</summary>
    public const string OpenSuggestionIndex = "ux_external_reviews_open_suggestion";

    /// <summary>At most one open item per group (sender + group key).</summary>
    public const string OpenGroupIndex = "ux_external_reviews_open_group";

    /// <summary>At most one open item per label plan.</summary>
    public const string OpenLabelPlanIndex = "ux_external_reviews_open_label_plan";

    /// <summary>At most one open item per filter finding.</summary>
    public const string OpenFilterFindingIndex = "ux_external_reviews_open_filter_finding";

    private const string OpenFilter = "(status IN ('queued', 'running') OR (status = 'reviewed' AND resolution = 'none'))";

    public Guid Id { get; set; }
    public ExternalReviewTarget TargetType { get; set; }
    public Guid? SuggestionId { get; set; }
    public string SenderAddress { get; set; } = "";
    public string? GroupKey { get; set; }

    public Guid? LabelPlanId { get; set; }
    public Guid? FilterFindingId { get; set; }

    /// <summary>The analysis run the item was expanded from ("send a whole run").</summary>
    public Guid? RunId { get; set; }
    public ExternalReviewStatus Status { get; set; }

    /// <summary>One headless Claude Code run that took the item.</summary>
    public Guid? BatchId { get; set; }

    /// <summary><c>claude_code</c>, <c>claude_desktop</c> or <c>mcp</c>.</summary>
    public string? Reviewer { get; set; }
    public string? ReviewerModel { get; set; }
    public ReviewVerdict? Verdict { get; set; }
    /// <summary>
    /// The outcome Claude reviewed: its alternative, or for <c>agree</c> the outcome shown when the verdict came in
    /// (what accepting approves).
    /// </summary>
    public string? VerdictTopicLabel { get; set; }
    public bool? VerdictNeedsAction { get; set; }
    public bool? VerdictToBeDeleted { get; set; }

    /// <summary>
    /// With <see cref="VerdictDocumentTypeSet"/>, the document type of that outcome: what accepting sets (alternative) or
    /// matches (agree); null is none.
    /// </summary>
    public string? VerdictDocumentTypeLabel { get; set; }

    /// <summary>
    /// Whether <see cref="VerdictDocumentTypeLabel"/> was recorded. False (an alternative without a document type, or a
    /// row from before the column) keeps each member's own type on accept.
    /// </summary>
    public bool VerdictDocumentTypeSet { get; set; }
    public string? VerdictFilterCriteria { get; set; }

    /// <summary>A label plan's <c>alternative</c>: the label paths Claude proposes, as a JSON array.</summary>
    public string? AlternativeStructure { get; set; }
    public string? Reasoning { get; set; }
    public string? Error { get; set; }
    public ExternalReviewResolution Resolution { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExternalReviewRow>(e =>
        {
            e.ToTable("external_reviews");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.TargetType).IsRequired().HasConversion(new SnakeCaseEnumConverter<ExternalReviewTarget>());
            e.Property(r => r.SenderAddress).IsRequired();
            e.Property(r => r.Status).IsRequired().HasConversion(new SnakeCaseEnumConverter<ExternalReviewStatus>());
            e.Property(r => r.Verdict).HasConversion(new SnakeCaseEnumConverter<ReviewVerdict>());
            e.Property(r => r.VerdictFilterCriteria).HasColumnType("jsonb");
            e.Property(r => r.AlternativeStructure).HasColumnType("jsonb");
            e.Property(r => r.Resolution).IsRequired().HasConversion(new SnakeCaseEnumConverter<ExternalReviewResolution>());
            e.HasOne<SuggestionRow>().WithMany().HasForeignKey(r => r.SuggestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<LabelPlanRow>().WithMany().HasForeignKey(r => r.LabelPlanId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FilterFindingRow>().WithMany().HasForeignKey(r => r.FilterFindingId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.Status, r.CreatedAt });
            e.HasIndex(r => r.SuggestionId);
            e.HasIndex(r => new { r.SenderAddress, r.GroupKey });
            e.HasIndex(r => r.BatchId);
            e.HasIndex(r => r.SuggestionId, OpenSuggestionIndex).HasDatabaseName(OpenSuggestionIndex)
                .IsUnique().HasFilter($"target_type = 'suggestion' AND {OpenFilter}");
            e.HasIndex(r => new { r.SenderAddress, r.GroupKey }, OpenGroupIndex).HasDatabaseName(OpenGroupIndex)
                .IsUnique().HasFilter($"target_type = 'group' AND {OpenFilter}");
            e.HasIndex(r => r.LabelPlanId, OpenLabelPlanIndex).HasDatabaseName(OpenLabelPlanIndex)
                .IsUnique().HasFilter($"target_type = 'label_plan' AND {OpenFilter}");
            e.HasIndex(r => r.FilterFindingId, OpenFilterFindingIndex).HasDatabaseName(OpenFilterFindingIndex)
                .IsUnique().HasFilter($"target_type = 'filter_finding' AND {OpenFilter}");
        });
    }
}
