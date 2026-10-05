using System.Net;
using System.Text.Json;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Rules.Taxonomy;
using Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Rules.Labels;

/// <param name="ItemIds">The accepted items in apply order, fixed when the job was queued.</param>
/// <param name="Index">The item being applied.</param>
/// <param name="BatchId">The current merge item's <see cref="ActionKind.LabelMerge"/> batch, once its first chunk is prepared.</param>
/// <param name="Pending">The merge chunk whose log is written and whose <c>batchModify</c> may have been sent.</param>
public sealed record LabelPlanApplyCursor(Guid PlanId, Guid[] ItemIds, int Index = 0, Guid? BatchId = null, string[]? Pending = null);

/// <summary>
/// Applies a label plan's accepted items (DESIGN §6.5) in the order nest, merge, delete, one item per checkpoint; the
/// plan json records each item <c>applied</c> or <c>failed</c> with its error. A nest renames the label through
/// <c>labels.patch</c> (the id stays) and then each label under its old name. A merge moves the label's messages to the
/// target in logged, undoable chunks (see <c>LabelPlanApplyJob.Merge.cs</c>) and retargets its filters; the source label
/// stays and shows as empty in the next plan. A delete re-checks that the label is empty and that no active filter uses
/// it right before <c>labels.delete</c>. Renames, deletes and retargets are listed in History as
/// <see cref="ActionKind.LabelPlan"/> entries without undo. Every step re-runs safely, so a resume repeats the current item.
/// A taxonomy item (#366) creates its label (or uses its near-duplicate target) and proposes a policy for each of its
/// senders that has none, in the checkpoint that records the item applied.
/// </summary>
public sealed partial class LabelPlanApplyJob(
    AppDbContext db,
    IGmailClient gmail,
    LabelCatalog catalog,
    LabelResolver resolver,
    FilterService filters,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<LabelPlanApplyJob> logger) : IJobHandler, IJobCancelHook
{
    public const string JobType = RulesJobTypes.LabelPlanApply;
    public const string Queue = JobQueues.Apply;
    public const string NotEmptyError = "label not empty";
    public const string FilteredError = "filters target this label";
    public const string MissingError = "label no longer exists";

    public string Type => JobType;

    /// <summary>The ids of <paramref name="items"/> in apply order: create, nest, merge, then delete; plan order within a kind.</summary>
    public static Guid[] Order(IEnumerable<LabelPlanItem> items) =>
        [.. items.OrderBy(i => i.Kind switch
        {
            LabelPlanItemKind.Create => -1,
            LabelPlanItemKind.Nest => 0,
            LabelPlanItemKind.NearDuplicate => 1,
            _ => 2,
        }).Select(i => i.Id)];

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<LabelPlanApplyCursor>() ?? throw new JobRefusedException("The label plan job has no plan.");
        var plan = await db.LabelPlans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == cursor.PlanId, ct)
            ?? throw new JobRefusedException("The label plan no longer exists.");
        var items = plan.ReadItems().ToDictionary(i => i.Id);
        while (cursor.Index < cursor.ItemIds.Length)
        {
            var item = items.GetValueOrDefault(cursor.ItemIds[cursor.Index]);
            if (item is not null && item.Status == LabelPlanItemStatus.Accepted)
            {
                var signal = JobSignal.Continue;
                if (cursor.Pending is null)
                {
                    // Publishes the label being applied; a cancel or pause between items is honoured here.
                    signal = await ctx.CheckpointAsync(cursor, Progress(cursor, item.LabelName), ct);
                }

                if (signal == JobSignal.Continue)
                {
                    LabelPlanItemStatus status;
                    string? error;
                    (cursor, signal, status, error) = item.Kind switch
                    {
                        LabelPlanItemKind.Nest => Done(cursor, await NestAsync(ctx.JobId, item, ct)),
                        LabelPlanItemKind.Empty => Done(cursor, await DeleteAsync(ctx.JobId, item, ct)),
                        LabelPlanItemKind.Create => Done(cursor, await CreateAsync(ctx.JobId, item, ct)),
                        _ when item.IsTaxonomy => Done(cursor, (LabelPlanItemStatus.Applied, null)),
                        _ => await MergeAsync(ctx, cursor, item, ct),
                    };
                    if (signal == JobSignal.Continue)
                    {
                        var next = cursor with { Index = cursor.Index + 1, BatchId = null, Pending = null };
                        signal = await ctx.CheckpointAsync(
                            next, Progress(next, item.LabelName), async t =>
                            {
                                await SetItemAsync(cursor.PlanId, item.Id, status, error, t);
                                if (status == LabelPlanItemStatus.Applied)
                                {
                                    await ProposePoliciesAsync(item, t);
                                }
                            },
                            ct);
                        db.ChangeTracker.Clear();
                        cursor = next;
                    }
                }

                if (signal != JobSignal.Continue)
                {
                    if (signal == JobSignal.Cancel)
                    {
                        await ReturnToDraftAsync(cursor.PlanId, CancellationToken.None);
                    }

                    return;
                }
            }
            else
            {
                cursor = cursor with { Index = cursor.Index + 1, BatchId = null, Pending = null };
            }
        }

        await ctx.CompleteAsync(cursor, Progress(cursor, null), async t =>
        {
            var now = time.GetUtcNow();
            await db.LabelPlans.Where(p => p.Id == cursor.PlanId && p.Status == LabelPlanStatus.Applying)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, LabelPlanStatus.Applied).SetProperty(p => p.UpdatedAt, now), t);
        }, ct);
    }

    /// <summary>A merge chunk that may have reached Gmail must be finished by a resume, not cancelled.</summary>
    public bool AllowsCancel(string? cursor) =>
        cursor is null || JsonSerializer.Deserialize<LabelPlanApplyCursor>(cursor, JobRow.Json)?.Pending is null;

    /// <summary>The plan goes back to draft so the user can re-apply the items not applied yet.</summary>
    public async Task CancelledAsync(string? cursor, CancellationToken ct)
    {
        if (cursor is not null && JsonSerializer.Deserialize<LabelPlanApplyCursor>(cursor, JobRow.Json) is { } parsed)
        {
            await ReturnToDraftAsync(parsed.PlanId, ct);
        }
    }

    /// <summary>
    /// Renames the label to the proposed name unless it already has it, then each label under its old name; a label
    /// already under the new name is skipped, so a resume finishes what a failed run started.
    /// </summary>
    private async Task<(LabelPlanItemStatus, string?)> NestAsync(Guid jobId, LabelPlanItem item, CancellationToken ct)
    {
        if (item.ProposedName is not { } newName)
        {
            return (LabelPlanItemStatus.Failed, "no proposed name");
        }

        var labels = await catalog.RefreshAsync(ct);
        if (labels.FirstOrDefault(l => l.Type == GmailLabelType.User && l.Id == item.LabelId) is not { } label)
        {
            return (LabelPlanItemStatus.Failed, MissingError);
        }

        var oldPrefix = item.LabelName + "/";
        var newPrefix = newName + "/";
        var children = labels
            .Where(l => l.Type == GmailLabelType.User && l.Id != label.Id
                && l.Name.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
                && !l.Name.StartsWith(newPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return await RefusedAsItemAsync(async () =>
        {
            try
            {
                if (!string.Equals(label.Name, newName, StringComparison.Ordinal))
                {
                    await gmail.RenameLabelAsync(label.Id, newName, ct);
                    await RecordAsync(jobId, $"Renamed label {label.Name} to {newName}", ct);
                }

                foreach (var child in children)
                {
                    var childName = newPrefix + child.Name[oldPrefix.Length..];
                    await gmail.RenameLabelAsync(child.Id, childName, ct);
                    await RecordAsync(jobId, $"Renamed label {child.Name} to {childName}", ct);
                }
            }
            finally
            {
                catalog.Invalidate();
            }
        });
    }

    /// <summary>
    /// Deletes the label once no active filter uses it and Gmail says it is empty, both checked right before the call (the
    /// plan's own filter list may be stale); a label already gone counts as deleted.
    /// </summary>
    private async Task<(LabelPlanItemStatus, string?)> DeleteAsync(Guid jobId, LabelPlanItem item, CancellationToken ct)
    {
        var labels = await catalog.RefreshAsync(ct);
        if (labels.FirstOrDefault(l => l.Type == GmailLabelType.User && l.Id == item.LabelId) is not { } label)
        {
            return (LabelPlanItemStatus.Applied, null);
        }

        return await RefusedAsItemAsync(async () =>
        {
            var active = await db.Filters.AsNoTracking().Where(f => f.DeletedAt == null).ToListAsync(ct);
            if (active.Select(f => f.ReadAction()).Any(a => a.AddLabelIds.Concat(a.RemoveLabelIds).Contains(item.LabelId, StringComparer.Ordinal)))
            {
                throw new ItemRefusedException(FilteredError);
            }

            if (await gmail.GetLabelMessagesTotalAsync(item.LabelId, ct) > 0)
            {
                throw new ItemRefusedException(NotEmptyError);
            }

            try
            {
                await gmail.DeleteLabelAsync(item.LabelId, ct);
                await RecordAsync(jobId, $"Deleted empty label {label.Name}", ct);
            }
            finally
            {
                catalog.Invalidate();
            }
        });
    }

    /// <summary>Creates the taxonomy item's label unless it exists (also under another case); the create is listed in History.</summary>
    private async Task<(LabelPlanItemStatus, string?)> CreateAsync(Guid jobId, LabelPlanItem item, CancellationToken ct) =>
        await RefusedAsItemAsync(() => resolver.EnsureAsync(
            [item.ProposedName ?? item.LabelName], (label, t) => RecordAsync(jobId, $"Created label {label.Name}", t), ct));

    /// <summary>
    /// A <see cref="PolicyStatus.Proposed"/> archive policy (#358 reviews it) for each of a taxonomy item's senders
    /// without a policy of any status, labelled with the created label or the near-duplicate target.
    /// </summary>
    private async Task ProposePoliciesAsync(LabelPlanItem item, CancellationToken ct)
    {
        var topic = item.Kind == LabelPlanItemKind.Create ? item.ProposedName ?? item.LabelName : item.TargetLabelName;
        if (item.SenderKeys is not { Count: > 0 } || string.IsNullOrEmpty(topic))
        {
            return;
        }

        string[] keys = [.. item.SenderKeys.Distinct(StringComparer.Ordinal)];
        var existing = await db.SenderPolicies
            .Where(p => p.Scope == PolicyScope.Sender && keys.Contains(p.ScopeKey))
            .Select(p => p.ScopeKey)
            .ToListAsync(ct);
        string[] missing = [.. keys.Except(existing, StringComparer.Ordinal)];
        var names = (await db.Senders.AsNoTracking()
                .Where(s => missing.Contains(s.CanonicalAddress) && s.DisplayName != null)
                .OrderByDescending(s => s.TotalCount)
                .Select(s => new { s.CanonicalAddress, s.DisplayName })
                .ToListAsync(ct))
            .DistinctBy(s => s.CanonicalAddress)
            .ToDictionary(s => s.CanonicalAddress, s => s.DisplayName, StringComparer.Ordinal);
        var now = time.GetUtcNow();
        foreach (var key in missing)
        {
            db.SenderPolicies.Add(new SenderPolicyRow
            {
                Id = Guid.CreateVersion7(now),
                Scope = PolicyScope.Sender,
                ScopeKey = key,
                DisplayName = names.GetValueOrDefault(key),
                TopicLabel = topic,
                Action = PolicyAction.Archive,
                Confidence = TaxonomyPrompt.ProposalConfidence,
                Reason = TaxonomyPrompt.ProposalReason,
                PromptVersion = TaxonomyPrompt.Version,
                Status = PolicyStatus.Proposed,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs one item's Gmail calls: <c>applied</c>, or <c>failed</c> with the reason when Gmail refused this item (a name
    /// clash, an invalid name, a 4xx). Rate limits and a lost connection fail the job, which resumes the item.
    /// </summary>
    private static async Task<(LabelPlanItemStatus, string?)> RefusedAsItemAsync(Func<Task> calls)
    {
        try
        {
            await calls();
            return (LabelPlanItemStatus.Applied, null);
        }
        catch (Exception ex) when (ItemError(ex) is { } error)
        {
            return (LabelPlanItemStatus.Failed, error);
        }
    }

    /// <summary>The item's error for an exception that refuses only this item; null for one that must fail the job.</summary>
    private static string? ItemError(Exception ex) => ex switch
    {
        ItemRefusedException or GmailLabelExistsException or LabelLimitException or ArgumentException => ex.Message,
        GoogleApiException { HttpStatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError } api
            when !GmailRetryPolicy.IsRateLimited(api) => api.Error?.Message ?? api.Message,
        _ => null,
    };

    private static (LabelPlanApplyCursor, JobSignal, LabelPlanItemStatus, string?) Done(
        LabelPlanApplyCursor cursor, (LabelPlanItemStatus Status, string? Error) result) =>
        (cursor, JobSignal.Continue, result.Status, result.Error);

    /// <summary>Records one item's outcome in the plan json, under the plan row's lock.</summary>
    private async Task SetItemAsync(Guid planId, Guid itemId, LabelPlanItemStatus status, string? error, CancellationToken ct)
    {
        var row = (await db.LabelPlans.FromSql($"SELECT * FROM label_plans WHERE id = {planId} FOR UPDATE").ToListAsync(ct)).Single();
        row.WriteItems([.. row.ReadItems().Select(i => i.Id == itemId ? i with { Status = status, Error = error } : i)]);
        row.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Lists a Gmail change that moves no message (a rename, a delete, a filter retarget) in History as an
    /// <see cref="ActionKind.LabelPlan"/> entry, which has no undo; once per job, so a resumed item does not list it twice.
    /// </summary>
    private async Task RecordAsync(Guid jobId, string description, CancellationToken ct)
    {
        if (await db.ActionBatches.AnyAsync(b => b.JobId == jobId && b.Kind == ActionKind.LabelPlan && b.Description == description, ct))
        {
            return;
        }

        db.ActionBatches.Add(new ActionBatchRow
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            Kind = ActionKind.LabelPlan,
            Description = description,
            JobId = jobId,
            CreatedAt = time.GetUtcNow(),
        });

        // Gmail has changed: the entry is written even if the job is stopping.
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    /// <summary>Back to draft; discarded instead when the user built a newer draft meanwhile (one draft at most).</summary>
    private async Task ReturnToDraftAsync(Guid planId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var newer = await db.LabelPlans.AnyAsync(p => p.Status == LabelPlanStatus.Draft && p.Id != planId, ct);
        await db.LabelPlans.Where(p => p.Id == planId && p.Status == LabelPlanStatus.Applying)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, newer ? LabelPlanStatus.Discarded : LabelPlanStatus.Draft)
                .SetProperty(p => p.UpdatedAt, now), ct);
    }

    private static JobProgress Progress(LabelPlanApplyCursor cursor, string? label) =>
        new(cursor.Index, cursor.ItemIds.Length, label ?? $"Applied {cursor.ItemIds.Length} label plan item{(cursor.ItemIds.Length == 1 ? "" : "s")}");

    private sealed class ItemRefusedException(string message) : Exception(message);
}
