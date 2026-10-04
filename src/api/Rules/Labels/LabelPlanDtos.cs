namespace GmailOrganiser.Rules.Labels;

/// <param name="LabelCount">User labels the plan considered.</param>
/// <param name="JobId">The job applying the plan, once applied (#214).</param>
public sealed record LabelPlanDto(
    Guid Id,
    LabelPlanStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int LabelCount,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<LabelPlanItemDto> Items,
    Guid? JobId)
{
    public static LabelPlanDto From(LabelPlanRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new LabelPlanDto(
            row.Id, row.Status, row.CreatedAt, row.UpdatedAt, row.LabelCount, row.Warnings,
            [.. row.ReadItems().Select(LabelPlanItemDto.From)], row.JobId);
    }
}

/// <param name="ProposedName">The new name of a <c>nest</c> item.</param>
/// <param name="TargetLabelId">The label a <c>near_duplicate</c> item merges into.</param>
public sealed record LabelPlanItemDto(
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
    string? Error)
{
    public static LabelPlanItemDto From(LabelPlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new LabelPlanItemDto(
            item.Id, item.Kind, item.LabelId, item.LabelName, item.MessageCount, item.ProposedName, item.TargetLabelId,
            item.TargetLabelName, item.AffectedFilterIds, item.Rationale, item.Status, item.Error);
    }
}

/// <summary>An edit of one draft plan item; null fields stay unchanged.</summary>
/// <param name="Status"><c>accepted</c> or <c>rejected</c>.</param>
/// <param name="ProposedName">A new name for a <c>nest</c> item (a valid label path).</param>
/// <param name="TargetLabelId">Another user label for a <c>near_duplicate</c> item to merge into.</param>
public sealed record UpdatePlanItemRequest(string? Status, string? ProposedName, string? TargetLabelId);

/// <param name="JobId">The <c>label_plan_apply</c> job; its progress comes through the jobs hub.</param>
public sealed record LabelPlanApplyDto(Guid JobId);
