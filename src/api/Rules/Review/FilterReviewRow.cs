using System.Text.Json;
using System.Text.Json.Serialization;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Rules.Labels;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules.Review;

[JsonConverter(typeof(SnakeCaseJsonConverter<FilterFindingKind>))]
public enum FilterFindingKind
{
    /// <summary>Equal normalised criteria and equal actions.</summary>
    Duplicate,

    /// <summary>Equal normalised criteria, different actions.</summary>
    Overlap,

    /// <summary>The action names a label the mailbox no longer has.</summary>
    DeletedLabel,

    /// <summary>No stored message newer than <c>RulesStaleFilterDays</c> matches the criteria.</summary>
    NoRecentMatches,

    /// <summary>Single-sender <c>from</c> filters with identical actions.</summary>
    Mergeable,
}

[JsonConverter(typeof(SnakeCaseJsonConverter<FilterFixKind>))]
public enum FilterFixKind
{
    /// <summary>Reported only: no safe fix (a forwarding filter, or actions that cannot be combined).</summary>
    None,
    Delete,
    Merge,
    MergeActions,
    DropLabel,
}

[JsonConverter(typeof(SnakeCaseJsonConverter<FilterFindingStatus>))]
public enum FilterFindingStatus
{
    Open,
    Applied,
    Dismissed,

    /// <summary>A newer review replaced the finding while it was still open.</summary>
    Superseded,
}

/// <summary>The filter a fix creates (label ids, not names).</summary>
public sealed record FilterFixCreate(GmailFilterCriteria Criteria, GmailFilterAction Action);

/// <summary>A finding's proposed fix: create <see cref="Create"/> (if any) first, then delete <see cref="DeleteFilterIds"/>.</summary>
public sealed record FilterFix(FilterFixKind Kind, IReadOnlyList<string> DeleteFilterIds, FilterFixCreate? Create = null);

/// <summary>One filter review (<c>filter_reviews</c>); the summary columns are filled by the LLM summary (#215).</summary>
public sealed class FilterReviewRow
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Active filters the review checked.</summary>
    public int FilterCount { get; set; }
    public int FindingCount { get; set; }
    public string? Summary { get; set; }
    public string? SummaryModel { get; set; }
    public string? SummaryPromptVersion { get; set; }
    public string? SummaryError { get; set; }
    public DateTimeOffset? SummarisedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FilterReviewRow>(e =>
        {
            e.ToTable("filter_reviews");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.HasIndex(r => r.CreatedAt);
        });
        FilterFindingRow.Configure(modelBuilder);
    }
}

/// <summary>One finding of a filter review (<c>filter_findings</c>) with its fix; applied, dismissed or superseded at most once.</summary>
public sealed class FilterFindingRow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Guid Id { get; set; }
    public Guid ReviewId { get; set; }
    public FilterFindingKind Kind { get; set; }

    /// <summary>The filters the finding is about, in the order the description names them.</summary>
    public List<string> FilterIds { get; set; } = [];
    public string Description { get; set; } = "";

    /// <summary>The <see cref="FilterFix"/> as JSON; read it with <see cref="ReadFix"/>.</summary>
    public string Fix { get; set; } = "{}";
    public FilterFindingStatus Status { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>Why the last apply stopped; the finding stays open.</summary>
    public string? Error { get; set; }

    /// <summary>The filter an apply created (or found already created), so a re-apply never creates it twice.</summary>
    public string? CreatedFilterId { get; set; }

    /// <summary>The filters an apply has deleted so far; a re-apply skips them.</summary>
    public List<string> DeletedFilterIds { get; set; } = [];

    public FilterFix ReadFix() => JsonSerializer.Deserialize<FilterFix>(Fix, Json) ?? new FilterFix(FilterFixKind.Delete, []);

    public void WriteFix(FilterFix fix) => Fix = JsonSerializer.Serialize(fix, Json);

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FilterFindingRow>(e =>
        {
            e.ToTable("filter_findings");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Kind).HasConversion(new SnakeCaseEnumConverter<FilterFindingKind>()).IsRequired();
            e.Property(r => r.Status).HasConversion(new SnakeCaseEnumConverter<FilterFindingStatus>()).IsRequired();
            e.Property(r => r.FilterIds).IsRequired();
            e.Property(r => r.DeletedFilterIds).IsRequired();
            e.Property(r => r.Description).IsRequired();
            e.Property(r => r.Fix).HasColumnType("jsonb").IsRequired();
            e.HasOne<FilterReviewRow>().WithMany().HasForeignKey(r => r.ReviewId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.ReviewId, r.Status });
            e.HasIndex(r => r.Status);
        });
    }
}
