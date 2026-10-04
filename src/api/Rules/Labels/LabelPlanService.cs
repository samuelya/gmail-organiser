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
    /// <summary>Above this many user labels the counts come from the stored messages, not batched <c>labels.get</c> calls.</summary>
    public const int MaxCountedLabels = 1000;

    /// <summary>The <c>pg_advisory_xact_lock</c> key that serialises plan creation.</summary>
    private const long CreateLockKey = 0x6C61_6270_6C61;

    /// <summary>
    /// Builds a plan over the account's user labels and stores it as the draft; the previous draft becomes discarded.
    /// Every Gmail read happens before the first write, so a failure stores nothing. A label deleted in Gmail while the
    /// labels are counted is left out.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    public async Task<LabelPlanDto> CreateAsync(CancellationToken ct)
    {
        var labels = (await catalog.RefreshAsync(ct)).Where(l => l.Type == GmailLabelType.User).ToList();
        var warnings = new List<string>();
        List<(GmailLabel Label, long Count)> counted;
        var exact = labels.Count <= MaxCountedLabels;
        if (exact)
        {
            var totals = (await gmail.GetLabelsMessagesTotalAsync([.. labels.Select(l => l.Id)], ct))
                .ToDictionary(t => t.Id, t => t.MessagesTotal, StringComparer.Ordinal);
            counted = [.. labels.Where(l => totals.ContainsKey(l.Id)).Select(l => (l, totals[l.Id]))];
        }
        else
        {
            var local = await LocalCountsAsync(labels, ct);
            counted = [.. labels.Select(l => (l, local.GetValueOrDefault(l.Id)))];
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The account has {labels.Count} user labels (more than {MaxCountedLabels}), so message counts come from the fetched mail and may be lower than Gmail's; empty labels are not proposed."));
        }

        var items = LabelPlanBuilder.Build(counted, ProtectedNames(await settings.GetAsync(ct)), await FiltersByLabelIdAsync(ct), exact);
        var now = time.GetUtcNow();
        var row = new LabelPlanRow
        {
            Id = Guid.CreateVersion7(now),
            Status = LabelPlanStatus.Draft,
            Warnings = warnings,
            LabelCount = counted.Count,
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
    /// Like the builder's proposals, a merge target must be another non-protected user label that no item of the plan
    /// deletes or merges, and a proposed name must clash with no label, protected name or other item's proposed name.
    /// </summary>
    /// <exception cref="GmailNotConnectedException">A target or name is given and the app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">A target or name is given and Gmail kept rate-limiting.</exception>
    public async Task<PlanEditResult> UpdateItemAsync(Guid planId, Guid itemId, UpdatePlanItemRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<GmailLabel> catalogue = [];
        IReadOnlyCollection<string> protectedNames = [];
        if (request.TargetLabelId is not null || request.ProposedName is not null)
        {
            catalogue = await catalog.GetAsync(ct);
            protectedNames = ProtectedNames(await settings.GetAsync(ct));
        }

        GmailLabel? target = null;
        if (request.TargetLabelId is { } targetId)
        {
            target = catalogue.FirstOrDefault(l => l.Type == GmailLabelType.User && string.Equals(l.Id, targetId, StringComparison.Ordinal));
            if (target is null || protectedNames.Contains(target.Name, StringComparer.OrdinalIgnoreCase))
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

        // Whatever their status: accepting one later must not strand messages in a removed label.
        var removed = items.Where(i => i.Kind is LabelPlanItemKind.Empty or LabelPlanItemKind.NearDuplicate).ToList();
        if (target is not null && removed.Exists(i => string.Equals(i.LabelId, target.Id, StringComparison.Ordinal)))
        {
            return new PlanEditResult(PlanEditOutcome.Invalid, Detail: "The plan deletes or merges the target label.");
        }

        if (request.ProposedName is { } name && NameClash(name, item, items, removed, catalogue, protectedNames) is { } clash)
        {
            return new PlanEditResult(PlanEditOutcome.Invalid, Detail: clash);
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

    /// <summary>Marks the plan discarded (a no-op when it already is); refused once it is being applied.</summary>
    public async Task<PlanEditResult> DiscardAsync(Guid planId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await LockAsync(planId, ct) is not { } row)
        {
            return new PlanEditResult(PlanEditOutcome.NotFound);
        }

        if (row.Status is LabelPlanStatus.Applying or LabelPlanStatus.Applied)
        {
            return new PlanEditResult(PlanEditOutcome.Conflict, Detail: "The plan is being applied or has been applied.");
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

    /// <summary>Why <paramref name="name"/> cannot be <paramref name="item"/>'s new name, or null when it can.</summary>
    private static string? NameClash(
        string name, LabelPlanItem item, List<LabelPlanItem> items, List<LabelPlanItem> removed,
        IReadOnlyList<GmailLabel> catalogue, IReadOnlyCollection<string> protectedNames)
    {
        if (catalogue.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
            || protectedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return "A label with the proposed name already exists or is reserved by the app.";
        }

        if (items.Exists(i => i.Id != item.Id && string.Equals(i.ProposedName, name, StringComparison.OrdinalIgnoreCase)))
        {
            return "Another item of the plan already proposes this name.";
        }

        // A parent the plan deletes or merges would leave the renamed label under a removed one.
        return removed.Exists(i => name.StartsWith(i.LabelName + "/", StringComparison.OrdinalIgnoreCase))
            ? "The plan deletes or merges a parent of the proposed name."
            : null;
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
