using System.Globalization;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules.Labels;

public enum PlanEditOutcome
{
    Ok,
    NotFound,

    /// <summary>The plan is not in a state that allows the edit.</summary>
    Conflict,

    /// <summary>The edit does not fit the item (see <see cref="PlanEditResult.Detail"/>).</summary>
    Invalid,
}

public sealed record PlanEditResult(PlanEditOutcome Outcome, LabelPlanDto? Plan = null, string? Detail = null);

/// <summary>Builds, stores and edits label review plans (<c>label_plans</c>). No Gmail writes: #214 applies a plan.</summary>
public sealed class LabelPlanService(
    AppDbContext db, IGmailClient gmail, LabelCatalog catalog, ISettingsStore settings, TimeProvider time)
{
    /// <summary>Above this many user labels the counts come from the stored messages, not one <c>labels.get</c> each.</summary>
    public const int MaxCountedLabels = 1000;

    /// <summary>The <c>pg_advisory_xact_lock</c> key that serialises plan creation.</summary>
    private const long CreateLockKey = 0x6C61_6270_6C61;

    /// <summary>
    /// Builds a plan over the account's user labels and stores it as the draft; the previous draft becomes discarded.
    /// Every Gmail read happens before the first write, so a failure stores nothing.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    public async Task<LabelPlanDto> CreateAsync(CancellationToken ct)
    {
        var labels = (await catalog.RefreshAsync(ct)).Where(l => l.Type == GmailLabelType.User).ToList();
        var warnings = new List<string>();
        var counted = new List<(GmailLabel Label, long Count)>(labels.Count);
        if (labels.Count <= MaxCountedLabels)
        {
            foreach (var label in labels)
            {
                counted.Add((label, await gmail.GetLabelMessagesTotalAsync(label.Id, ct)));
            }
        }
        else
        {
            var local = await LocalCountsAsync(labels, ct);
            counted.AddRange(labels.Select(l => (l, local.GetValueOrDefault(l.Id))));
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The account has {labels.Count} user labels (more than {MaxCountedLabels}), so message counts come from the fetched mail and may be lower than Gmail's."));
        }

        var items = LabelPlanBuilder.Build(counted, ProtectedNames(await settings.GetAsync(ct)), await FiltersByLabelIdAsync(ct));
        var now = time.GetUtcNow();
        var row = new LabelPlanRow
        {
            Id = Guid.CreateVersion7(now),
            Status = LabelPlanStatus.Draft,
            Warnings = warnings,
            LabelCount = labels.Count,
            CreatedAt = now,
            UpdatedAt = now,
        };
        row.WriteItems(items);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({CreateLockKey})", ct);
        await db.LabelPlans.Where(p => p.Status == LabelPlanStatus.Draft)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, LabelPlanStatus.Discarded).SetProperty(p => p.UpdatedAt, now), ct);
        db.LabelPlans.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LabelPlanDto.From(row);
    }

    /// <summary>The newest plan that is not discarded, or null.</summary>
    public async Task<LabelPlanDto?> LatestAsync(CancellationToken ct) =>
        await db.LabelPlans.AsNoTracking()
            .Where(p => p.Status != LabelPlanStatus.Discarded)
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct) is { } row
            ? LabelPlanDto.From(row)
            : null;

    public async Task<LabelPlanDto?> GetAsync(Guid id, CancellationToken ct) =>
        await db.LabelPlans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) is { } row ? LabelPlanDto.From(row) : null;

    /// <summary>
    /// Applies <paramref name="request"/> (already checked for a valid status and path) to one item of a draft plan.
    /// A merge target must be another non-protected user label.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">A target is given and the app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">A target is given and Gmail kept rate-limiting.</exception>
    public async Task<PlanEditResult> UpdateItemAsync(Guid planId, Guid itemId, UpdatePlanItemRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        GmailLabel? target = null;
        if (request.TargetLabelId is { } targetId)
        {
            target = (await catalog.GetAsync(ct)).FirstOrDefault(l => l.Type == GmailLabelType.User && string.Equals(l.Id, targetId, StringComparison.Ordinal));
            if (target is null || ProtectedNames(await settings.GetAsync(ct)).Contains(target.Name, StringComparer.OrdinalIgnoreCase))
            {
                return new PlanEditResult(PlanEditOutcome.Invalid, Detail: "The target is not a user label the plan can merge into.");
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(planId, ct) is not { } row)
        {
            return new PlanEditResult(PlanEditOutcome.NotFound);
        }

        if (row.Status != LabelPlanStatus.Draft)
        {
            return new PlanEditResult(PlanEditOutcome.Conflict, Detail: "Only a draft plan can be edited.");
        }

        var items = row.ReadItems().ToList();
        var index = items.FindIndex(i => i.Id == itemId);
        if (index < 0)
        {
            return new PlanEditResult(PlanEditOutcome.NotFound);
        }

        var item = items[index];
        if (request.ProposedName is not null && item.Kind != LabelPlanItemKind.Nest)
        {
            return new PlanEditResult(PlanEditOutcome.Invalid, Detail: "Only a nest item has a proposed name.");
        }

        if (target is not null && item.Kind != LabelPlanItemKind.NearDuplicate)
        {
            return new PlanEditResult(PlanEditOutcome.Invalid, Detail: "Only a near-duplicate item has a target label.");
        }

        if (target is not null && string.Equals(target.Id, item.LabelId, StringComparison.Ordinal))
        {
            return new PlanEditResult(PlanEditOutcome.Invalid, Detail: "A label cannot merge into itself.");
        }

        items[index] = item with
        {
            Status = request.Status is { } status ? ParseStatus(status)!.Value : item.Status,
            ProposedName = request.ProposedName ?? item.ProposedName,
            TargetLabelId = target?.Id ?? item.TargetLabelId,
            TargetLabelName = target?.Name ?? item.TargetLabelName,
        };
        row.WriteItems(items);
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PlanEditResult(PlanEditOutcome.Ok, LabelPlanDto.From(row));
    }

    /// <summary>Marks the plan discarded (a no-op when it already is); refused while it is being applied.</summary>
    public async Task<PlanEditResult> DiscardAsync(Guid planId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(planId, ct) is not { } row)
        {
            return new PlanEditResult(PlanEditOutcome.NotFound);
        }

        if (row.Status == LabelPlanStatus.Applying)
        {
            return new PlanEditResult(PlanEditOutcome.Conflict, Detail: "The plan is being applied.");
        }

        if (row.Status != LabelPlanStatus.Discarded)
        {
            row.Status = LabelPlanStatus.Discarded;
            row.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new PlanEditResult(PlanEditOutcome.Ok, LabelPlanDto.From(row));
    }

    /// <summary><c>accepted</c> or <c>rejected</c>; null for anything else.</summary>
    public static LabelPlanItemStatus? ParseStatus(string status) => status switch
    {
        "accepted" => LabelPlanItemStatus.Accepted,
        "rejected" => LabelPlanItemStatus.Rejected,
        _ => null,
    };

    /// <summary>The labels a plan never proposes or merges into: the action and delete labels.</summary>
    /// <remarks>The Apps Script rule and keep-in-inbox labels join this list with their settings (#210).</remarks>
    public static IReadOnlyCollection<string> ProtectedNames(AppSettings app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return [app.ActionLabelName, app.DeleteLabelName];
    }

    private async Task<LabelPlanRow?> LockAsync(Guid id, CancellationToken ct) =>
        (await db.LabelPlans.FromSql($"SELECT * FROM label_plans WHERE id = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();

    private async Task<Dictionary<string, IReadOnlyList<string>>> FiltersByLabelIdAsync(CancellationToken ct)
    {
        var filters = await db.Filters.AsNoTracking().Where(f => f.DeletedAt == null).OrderBy(f => f.Id).ToListAsync(ct);
        return filters
            .SelectMany(f =>
            {
                var action = f.ReadAction();
                return action.AddLabelIds.Concat(action.RemoveLabelIds).Distinct(StringComparer.Ordinal).Select(id => (LabelId: id, FilterId: f.Id));
            })
            .GroupBy(p => p.LabelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(p => p.FilterId)], StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, long>> LocalCountsAsync(List<GmailLabel> labels, CancellationToken ct)
    {
        var ids = labels.Select(l => l.Id).ToList();
        return await db.Messages.AsNoTracking()
            .Where(m => !m.DeletedInGmail)
            .SelectMany(m => m.LabelIds)
            .Where(id => ids.Contains(id))
            .GroupBy(id => id)
            .Select(g => new { Id = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
    }
}
