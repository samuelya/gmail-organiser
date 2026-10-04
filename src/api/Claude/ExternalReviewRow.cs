using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Claude;

/// <summary>What a Claude review item points at; M6 adds label plans and filter findings.</summary>
public enum ExternalReviewTarget
{
    Suggestion,
    Group,
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
/// One Claude review item (<c>external_reviews</c>): a local suggestion, or a review group (sender + group key), and
/// Claude's verdict once submitted. Open items are <c>Queued|Running</c>, or <c>Reviewed</c> without a resolution.
/// </summary>
public sealed class ExternalReviewRow
{
    /// <summary>At most one open item per suggestion.</summary>
    public const string OpenSuggestionIndex = "ux_external_reviews_open_suggestion";

    /// <summary>At most one open item per group (sender + group key).</summary>
    public const string OpenGroupIndex = "ux_external_reviews_open_group";

    private const string OpenFilter = "(status IN ('queued', 'running') OR (status = 'reviewed' AND resolution = 'none'))";

    public Guid Id { get; set; }
    public ExternalReviewTarget TargetType { get; set; }
    public Guid? SuggestionId { get; set; }
    public string SenderAddress { get; set; } = "";
    public string? GroupKey { get; set; }

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
            e.Property(r => r.Resolution).IsRequired().HasConversion(new SnakeCaseEnumConverter<ExternalReviewResolution>());
            e.HasOne<SuggestionRow>().WithMany().HasForeignKey(r => r.SuggestionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.Status, r.CreatedAt });
            e.HasIndex(r => r.SuggestionId);
            e.HasIndex(r => new { r.SenderAddress, r.GroupKey });
            e.HasIndex(r => r.BatchId);
            e.HasIndex(r => r.SuggestionId, OpenSuggestionIndex).HasDatabaseName(OpenSuggestionIndex)
                .IsUnique().HasFilter($"target_type = 'suggestion' AND {OpenFilter}");
            e.HasIndex(r => new { r.SenderAddress, r.GroupKey }, OpenGroupIndex).HasDatabaseName(OpenGroupIndex)
                .IsUnique().HasFilter($"target_type = 'group' AND {OpenFilter}");
        });
    }
}
