using System.Text.Json;
using System.Text.Json.Serialization;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules.Labels;

[JsonConverter(typeof(SnakeCaseJsonConverter<LabelPlanStatus>))]
public enum LabelPlanStatus
{
    Draft,
    Applying,
    Applied,
    Discarded,
}

[JsonConverter(typeof(SnakeCaseJsonConverter<LabelPlanItemKind>))]
public enum LabelPlanItemKind
{
    /// <summary>The label has no messages: proposed delete.</summary>
    Empty,

    /// <summary>The label's name nearly equals another's: proposed merge into <see cref="LabelPlanItem.TargetLabelId"/>.</summary>
    NearDuplicate,

    /// <summary>A flat <c>X-Y</c> label: proposed rename to <see cref="LabelPlanItem.ProposedName"/> (<c>X/Y</c>).</summary>
    Nest,
}

[JsonConverter(typeof(SnakeCaseJsonConverter<LabelPlanItemStatus>))]
public enum LabelPlanItemStatus
{
    Proposed,
    Accepted,
    Rejected,
    Applied,
    Failed,
}

/// <summary>Serialises an enum as its snake_case name, as the API and the <c>items</c> jsonb store it.</summary>
public sealed class SnakeCaseJsonConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>One proposal of a label plan, stored in <see cref="LabelPlanRow.Items"/>.</summary>
/// <param name="MessageCount">The label's message count when the plan was built.</param>
/// <param name="AffectedFilterIds">Active filters whose action adds or removes the label.</param>
public sealed record LabelPlanItem(
    Guid Id,
    LabelPlanItemKind Kind,
    string LabelId,
    string LabelName,
    long MessageCount,
    string? ProposedName,
    string? TargetLabelId,
    string? TargetLabelName,
    IReadOnlyList<string> AffectedFilterIds,
    string Rationale,
    LabelPlanItemStatus Status,
    string? Error = null);

/// <summary>
/// One label review plan (<c>label_plans</c>): deterministic proposals over the account's user labels that the user edits,
/// accepts or rejects. At most one plan is a <see cref="LabelPlanStatus.Draft"/>.
/// </summary>
public sealed class LabelPlanRow
{
    /// <summary>At most one draft plan.</summary>
    public const string DraftIndex = "ux_label_plans_draft";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Guid Id { get; set; }
    public LabelPlanStatus Status { get; set; }

    /// <summary>The <see cref="LabelPlanItem"/> list as JSON; read it with <see cref="ReadItems"/>.</summary>
    public string Items { get; set; } = "[]";
    public List<string> Warnings { get; set; } = [];

    /// <summary>User labels the plan considered.</summary>
    public int LabelCount { get; set; }

    /// <summary>The job applying the plan (#214).</summary>
    public Guid? JobId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public IReadOnlyList<LabelPlanItem> ReadItems() => JsonSerializer.Deserialize<List<LabelPlanItem>>(Items, Json) ?? [];

    public void WriteItems(IReadOnlyList<LabelPlanItem> items) => Items = JsonSerializer.Serialize(items, Json);

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LabelPlanRow>(e =>
        {
            e.ToTable("label_plans");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Status).HasConversion(new SnakeCaseEnumConverter<LabelPlanStatus>()).IsRequired();
            e.Property(r => r.Items).HasColumnType("jsonb").IsRequired();
            e.Property(r => r.Warnings).IsRequired();
            e.HasIndex(r => r.Status).HasDatabaseName(DraftIndex).IsUnique().HasFilter("status = 'draft'");
            e.HasIndex(r => r.CreatedAt);
        });
    }
}
