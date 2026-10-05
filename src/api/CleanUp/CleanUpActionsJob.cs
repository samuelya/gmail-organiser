using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.CleanUp;

public static class CleanUpJobTypes
{
    /// <summary>Trashes or unmarks delete-labelled messages (<see cref="CleanUpActionsJob"/>); its dedup key is the batch id.</summary>
    public const string Actions = "cleanup_actions";
}

/// <param name="Kind"><see cref="ActionKind.Trash"/> or <see cref="ActionKind.Unmark"/>.</param>
/// <param name="DeleteLabelId">The delete label's Gmail id when the batch was created.</param>
/// <param name="Gone">Messages Gmail no longer has; marked deleted, not logged.</param>
/// <param name="Pending">The chunk whose log is written and whose <c>batchModify</c> may have been sent.</param>
/// <param name="Skipped">Messages Gmail refused one by one; not logged and not retried by this job.</param>
public sealed record CleanUpCursor(
    Guid BatchId,
    ActionKind Kind,
    string DeleteLabelId,
    CleanUpSelection Selection,
    bool IncludeProtected,
    int ChunksDone = 0,
    int MessagesDone = 0,
    int Gone = 0,
    CleanUpChunk? Pending = null,
    string[]? Skipped = null);

/// <summary>One <c>batchModify</c> call: the same label ids added and removed on every message.</summary>
public sealed record CleanUpChunk(string[] MessageIds, string[] Add, string[] Remove);

/// <summary>
/// Moves the selected delete-labelled messages to Trash (adds <c>TRASH</c>, removes <c>INBOX</c>; the delete label
/// stays) or removes the delete label from them (DESIGN §3.3, §3.6). Each chunk goes through <see cref="ChunkSender"/>:
/// the first transaction re-checks the messages (still labelled, not in Trash, unprotected unless the batch includes
/// protected mail) before the undo log; the second also stores <c>deleted_in_gmail</c> and sender stats.
/// </summary>
public sealed class CleanUpActionsJob(
    AppDbContext db,
    IGmailClient gmail,
    LabelCatalog catalog,
    ISettingsStore settingsStore,
    SenderStatsUpdater senders,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<CleanUpActionsJob> logger) : IJobHandler, IJobCancelHook
{
    public const string JobType = CleanUpJobTypes.Actions;
    public const string Queue = JobQueues.Apply;
    public const string ProtectedReason = "protected";

    // Selected messages the current plan leaves alone because they are protected; reported in every progress update.
    private int skippedProtected;

    public string Type => JobType;

    private ChunkSender Chunks => field ??= new(db, gmail, time, logger, "clean-up");

    public static string Describe(ActionKind kind, int count, string? senderAddress) =>
        $"{(kind == ActionKind.Trash ? "Delete" : "Removed from clean-up")}: {count} message{(count == 1 ? "" : "s")}"
        + (senderAddress is null ? "" : $" from {senderAddress}");

    /// <summary>Whether the batch changes <paramref name="m"/>: Delete skips protected mail unless told otherwise.</summary>
    public static bool Covers(ActionKind kind, bool includeProtected, MessageRow m, Allowlist allowlist, ProtectionSettings rules) =>
        kind == ActionKind.Unmark || includeProtected || !MessageProtection.IsProtected(m, allowlist, rules);

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<CleanUpCursor>() ?? throw new JobRefusedException("The clean-up job has no batch.");
        if (!await db.ActionBatches.AnyAsync(b => b.Id == cursor.BatchId, ct))
        {
            throw new JobRefusedException("The action batch no longer exists.");
        }

        var names = (await catalog.GetAsync(ct)).ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        var settings = await settingsStore.GetAsync(ct);
        // The pending chunk's rows aren't stored as changed yet, so the plan would cover them a second time.
        var plan = Without(await PlanAsync(cursor, settings, ct), cursor.Pending);
        var total = Done(cursor) + (cursor.Pending?.MessageIds.Length ?? 0) + plan.Sum(c => c.MessageIds.Length);
        if (cursor.Pending is not null)
        {
            (cursor, var signal, total) = await SendAsync(ctx, cursor, resent: true, total, ct);
            if (signal != JobSignal.Continue)
            {
                return;
            }
        }

        while (true)
        {
            var replan = false;
            foreach (var chunk in plan)
            {
                if (await PrepareAsync(ctx, cursor, chunk, settings, names, total, ct) is not { } prepared)
                {
                    replan = true;
                    break;
                }

                (cursor, var signal, total) = await SendAsync(ctx, prepared, resent: false, total, ct);
                if (signal != JobSignal.Continue)
                {
                    return;
                }
            }

            if (!replan)
            {
                break;
            }

            // Fresh settings: the locked re-check reads them as stored now, so the new plan must agree with it.
            settings = await settingsStore.GetAsync(ct);
            plan = await PlanAsync(cursor, settings, ct);
            total = Done(cursor) + plan.Sum(c => c.MessageIds.Length);
        }

        await ctx.CompleteAsync(cursor, Progress(cursor, total), _ => Task.CompletedTask, ct);
    }

    /// <summary>A chunk that may have reached Gmail must be finished by a resume, not cancelled.</summary>
    public bool AllowsCancel(string? cursor) =>
        cursor is null || JsonSerializer.Deserialize<CleanUpCursor>(cursor, JobRow.Json)?.Pending is null;

    /// <summary>Nothing to follow up: what was sent is logged and undoable from History.</summary>
    public Task CancelledAsync(string? cursor, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The label ids the batch adds to and removes from <paramref name="m"/>.</summary>
    public static (string[] Add, string[] Remove) Change(ActionKind kind, string deleteLabelId, MessageRow m) =>
        kind == ActionKind.Trash
            ? ([CleanUpQuery.TrashLabel], m.LabelIds.Contains(CleanUpQuery.InboxLabel, StringComparer.Ordinal) ? [CleanUpQuery.InboxLabel] : [])
            : ([], [deleteLabelId]);

    /// <summary>Chunks of the selected messages still to change, in id order; protected ones are counted, not planned.</summary>
    private async Task<List<CleanUpChunk>> PlanAsync(CleanUpCursor cursor, AppSettings settings, CancellationToken ct)
    {
        var skipped = cursor.Skipped ?? [];
        var rows = await CleanUpQuery.Selected(db, cursor.DeleteLabelId, cursor.Selection)
            .Where(m => !skipped.Contains(m.Id))
            .OrderBy(m => m.Id)
            .Select(m => new MessageRow
            {
                Id = m.Id,
                FromAddress = m.FromAddress,
                LabelIds = m.LabelIds,
                HasAttachment = m.HasAttachment,
                ThreadReplied = m.ThreadReplied,
            })
            .AsNoTracking()
            .ToListAsync(ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, ct);
        var covered = rows.Where(m => Covers(cursor.Kind, cursor.IncludeProtected, m, allowlist, settings.Protection)).ToList();
        skippedProtected = rows.Count - covered.Count;
        var changes = covered.Select(m => (m.Id, Change: Change(cursor.Kind, cursor.DeleteLabelId, m)));
        return [.. LabelChunks.Group(changes, c => c.Change.Add, c => c.Change.Remove, gmailOptions.Value.BatchModifyMaxIds)
            .Select(c => new CleanUpChunk([.. c.Items.Select(i => i.Id)], c.Add, c.Remove))];
    }

    /// <summary>
    /// Locks the chunk's messages, re-checks each against the batch (state can change between the click and the
    /// send), writes the undo log and checkpoints the chunk as pending, in one transaction. Null when anything changed
    /// since planning: nothing was written, the caller replans.
    /// </summary>
    private async Task<CleanUpCursor?> PrepareAsync(
        JobContext ctx,
        CleanUpCursor cursor,
        CleanUpChunk chunk,
        AppSettings settings,
        IReadOnlyDictionary<string, string> names,
        int total,
        CancellationToken ct)
    {
        var next = cursor with { Pending = chunk };
        var prepared = await Chunks.PrepareAsync(
            ctx, next, Progress(cursor, total), cursor.BatchId, chunk.MessageIds, chunk.Add, chunk.Remove, names, async (messages, t) =>
            {
                // Both halves as stored now: an address or a domain allowlisted since the job started is honoured.
                var allowlist = await AllowlistLoader.LoadAsync(
                    db, await settingsStore.GetAsync(t), [.. messages.Select(m => m.FromAddress).Distinct()], t);
                return messages.All(m => Fits(m, allowlist));
            }, ct);
        return prepared ? next : null;

        bool Fits(MessageRow m, Allowlist allowlist)
        {
            var (add, remove) = Change(cursor.Kind, cursor.DeleteLabelId, m);
            return !m.DeletedInGmail
                && m.LabelIds.Contains(cursor.DeleteLabelId, StringComparer.Ordinal)
                && !m.LabelIds.Contains(CleanUpQuery.TrashLabel, StringComparer.Ordinal)
                && Covers(cursor.Kind, cursor.IncludeProtected, m, allowlist, settings.Protection)
                && LabelChunks.Sorted(add).SequenceEqual(chunk.Add)
                && LabelChunks.Sorted(remove).SequenceEqual(chunk.Remove);
        }
    }

    /// <summary>
    /// Sends the pending chunk (<see cref="ChunkSender.SendAsync"/>), then stores its result, the Trash state and
    /// sender stats, and clears it.
    /// </summary>
    private async Task<(CleanUpCursor Cursor, JobSignal Signal, int Total)> SendAsync(
        JobContext ctx, CleanUpCursor cursor, bool resent, int total, CancellationToken ct)
    {
        var chunk = cursor.Pending!;
        var result = await Chunks.SendAsync(
            ctx, cursor.BatchId, chunk.MessageIds, chunk.Add, chunk.Remove, resent, cursor with { Pending = null }, Progress(cursor, total), ct);
        var (changed, gone, rejected) = result;
        var done = cursor with
        {
            Pending = null,
            ChunksDone = cursor.ChunksDone + 1,
            MessagesDone = cursor.MessagesDone + changed.Length,
            Gone = cursor.Gone + gone.Length,
            Skipped = ChunkSender.Skipped(cursor.Skipped, result),
        };
        total -= rejected.Length;
        var signal = await Chunks.StoreAsync(
            ctx, done, Progress(done, total), cursor.BatchId, chunk.Add, chunk.Remove, result,
            m => m.DeletedInGmail = m.LabelIds.Contains(CleanUpQuery.TrashLabel, StringComparer.Ordinal),
            async t =>
            {
                if (cursor.Kind == ActionKind.Trash || gone.Length > 0)
                {
                    var addresses = await db.Messages.Where(m => changed.Contains(m.Id) || gone.Contains(m.Id))
                        .Select(m => m.FromAddress).ToListAsync(t);
                    await senders.UpdateAsync(addresses, t);
                }
            },
            ct);
        return (done, signal, total);
    }

    private static List<CleanUpChunk> Without(List<CleanUpChunk> plan, CleanUpChunk? pending)
    {
        if (pending is null)
        {
            return plan;
        }

        var sent = pending.MessageIds.ToHashSet(StringComparer.Ordinal);
        return [.. plan.Select(c => c with { MessageIds = [.. c.MessageIds.Where(id => !sent.Contains(id))] })
            .Where(c => c.MessageIds.Length > 0)];
    }

    private static int Done(CleanUpCursor cursor) => cursor.MessagesDone + cursor.Gone;

    /// <summary>Messages changed of the total, and every skip with its reason (protected first).</summary>
    private JobProgress Progress(CleanUpCursor cursor, int total) =>
        new(Done(cursor), total, ChunkSender.Message(
            cursor.Kind == ActionKind.Trash ? "Moved to Trash" : "Removed from clean-up",
            cursor.MessagesDone, total, skippedProtected, ProtectedReason, cursor.Gone, cursor.Skipped));
}
