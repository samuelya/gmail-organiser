using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.CleanUp;

/// <summary>One <c>batchModify</c> call; <paramref name="LastId"/> is set on the last call of a 500-id block only.</summary>
public sealed record RetentionChunk(string[] MessageIds, string[] Add, string[] Remove, string? LastId);

/// <param name="BatchId">The run's History batch, created by the first checkpoint; null before.</param>
/// <param name="DeleteLabelId">The delete label's Gmail id, resolved (created if needed) once the batch exists.</param>
/// <param name="LastId">The highest message id (ordinal) of the last block stored; only later ids are planned.</param>
/// <param name="Gone">Messages Gmail no longer has; marked deleted, not logged.</param>
/// <param name="Pending">The chunk whose log is written and whose <c>batchModify</c> may have been sent.</param>
/// <param name="Skipped">Messages Gmail refused one by one; not logged and not retried by this job.</param>
public sealed record RetentionCursor(
    Guid? BatchId = null,
    string? DeleteLabelId = null,
    string? LastId = null,
    int ChunksDone = 0,
    int MessagesDone = 0,
    int Gone = 0,
    RetentionChunk? Pending = null,
    string[]? Skipped = null);

/// <summary>
/// The retention sweep (#368, DESIGN §6.4): live applied mail, not in Trash and not delete-labelled, whose effective
/// retention has passed gets the delete label and leaves the inbox; the portal never trashes. Retention is the
/// suggestion's approved sub-rule's <c>RetentionDays</c>, else its approved policy's, else the mail type's days in
/// <see cref="RetentionSettings"/>; an approved keep rule or policy, protected mail and transactional mail are never
/// marked. Blocks of <see cref="BlockSize"/> ids in id order, split by whether <c>INBOX</c> is present so undo restores
/// exactly, each through <see cref="ChunkSender"/> as in <see cref="Senders.SenderArchiveJob"/>. Marked mail drops out of
/// the plan, so a resume never marks twice; <see cref="UndoActionsJob"/> reverts the run's batch.
/// </summary>
public sealed class RetentionSweepJob(
    AppDbContext db,
    IGmailClient gmail,
    LabelCatalog catalog,
    LabelResolver labels,
    ISettingsStore settingsStore,
    TransactionalGuard guard,
    RepliedThreadChecker repliedThreads,
    SenderStatsUpdater senders,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<RetentionSweepJob> logger) : IJobHandler, IJobCancelHook
{
    public const string JobType = "retention_sweep";
    public const string Queue = JobQueues.Apply;
    public const int BlockSize = 500;
    public const string ProtectedReason = "protected";

    // Expired messages left alone because they are protected or transactional; reported in every progress update.
    private int skippedProtected;

    public string Type => JobType;

    private ChunkSender Chunks => field ??= new(db, gmail, time, logger, "retention sweep");

    public static string Describe(int messages, string deleteLabelName) =>
        $"Retention: {messages} expired message{(messages == 1 ? "" : "s")} marked {deleteLabelName}";

    /// <summary>
    /// The expired candidates the sweep marks, in id order, and how many expired ones it skips as protected or
    /// transactional. <paramref name="ids"/> narrows the query to those messages.
    /// </summary>
    public static async Task<(List<MessageRow> Covered, int Protected)> PlanAsync(
        AppDbContext db, TransactionalGuard guard, AppSettings settings, string? deleteLabelId, DateTimeOffset now,
        CancellationToken ct, string[]? ids = null)
    {
        var expired = await ExpiredAsync(db, settings, deleteLabelId, now, ids, ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, [.. expired.Select(m => m.FromAddress).Distinct()], ct);
        var covered = expired.Where(m => !MessageProtection.IsProtected(m, allowlist, settings.Protection) && !guard.IsTransactional(m))
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
        return (covered, expired.Count - covered.Count);
    }

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<RetentionCursor>() ?? new RetentionCursor();
        var settings = await settingsStore.GetAsync(ct);
        if (cursor.BatchId is null)
        {
            var existing = (await catalog.FindByNameAsync(settings.DeleteLabelName, ct))?.Id;
            var (covered, protectedCount) = await PlanAsync(db, guard, settings, existing, time.GetUtcNow(), ct);
            skippedProtected = protectedCount;
            if (covered.Count == 0)
            {
                await ctx.CompleteAsync(cursor, Progress(cursor, 0), _ => Task.CompletedTask, ct);
                return;
            }

            var now = time.GetUtcNow();
            var batch = new ActionBatchRow
            {
                Id = Guid.CreateVersion7(now),
                Kind = ActionKind.Retention,
                Description = Describe(covered.Count, settings.DeleteLabelName),
                JobId = ctx.JobId,
                CreatedAt = now,
            };
            cursor = cursor with { BatchId = batch.Id };
            var signal = await ctx.CheckpointAsync(cursor, Progress(cursor, covered.Count), async t =>
            {
                db.ActionBatches.Add(batch);
                await db.SaveChangesAsync(t);
            }, ct);
            db.ChangeTracker.Clear();
            if (signal != JobSignal.Continue)
            {
                return;
            }
        }

        var batchId = cursor.BatchId!.Value;
        if (!await db.ActionBatches.AnyAsync(b => b.Id == batchId, ct))
        {
            throw new JobRefusedException("The action batch no longer exists.");
        }

        if (cursor.DeleteLabelId is null)
        {
            if (!LabelResolver.IsValid(settings.DeleteLabelName))
            {
                throw new JobRefusedException($"The {nameof(AppSettings.DeleteLabelName)} setting is not a valid Gmail label.");
            }

            var resolved = await labels.EnsureAsync([settings.DeleteLabelName], (label, _) => RecordCreatedAsync(batchId, label), ct);
            cursor = cursor with { DeleteLabelId = resolved[settings.DeleteLabelName] };
            if (await ctx.CheckpointAsync(cursor, Progress(cursor, cursor.MessagesDone), ct) != JobSignal.Continue)
            {
                return;
            }
        }

        var plan = await ChunksAsync(cursor, ct);
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
                await CheckThreadsAsync(chunk.MessageIds, ct);
                if (await PrepareAsync(ctx, cursor, chunk, total, ct) is not { } prepared)
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

            plan = await ChunksAsync(cursor, ct);
            total = Done(cursor) + plan.Sum(c => c.MessageIds.Length);
        }

        var deleteLabelName = (await settingsStore.GetAsync(ct)).DeleteLabelName;
        await ctx.CompleteAsync(cursor, Progress(cursor, total), t => db.ActionBatches.Where(b => b.Id == batchId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Description, Describe(cursor.MessagesDone, deleteLabelName)), t), ct);
    }

    /// <summary>A chunk that may have reached Gmail must be finished by a resume, not cancelled.</summary>
    public bool AllowsCancel(string? cursor) =>
        cursor is null || JsonSerializer.Deserialize<RetentionCursor>(cursor, JobRow.Json)?.Pending is null;

    /// <summary>Nothing to follow up: what was sent is logged and undoable from History.</summary>
    public Task CancelledAsync(string? cursor, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The delete label added; <c>INBOX</c> removed only where present, so undo never adds it to archived mail.</summary>
    public static (string[] Add, string[] Remove) Change(string deleteLabelId, MessageRow m) =>
        ([deleteLabelId], m.LabelIds.Contains(CleanUpQuery.InboxLabel, StringComparer.Ordinal) ? [CleanUpQuery.InboxLabel] : []);

    /// <summary>
    /// Live applied messages, not in Trash and not delete-labelled, past their effective retention, with the fields
    /// protection and the transactional guard read. An approved keep rule or policy means no retention.
    /// </summary>
    private static async Task<List<MessageRow>> ExpiredAsync(
        AppDbContext db, AppSettings settings, string? deleteLabelId, DateTimeOffset now, string[]? ids, CancellationToken ct)
    {
        var newest = now.AddDays(-RetentionSettings.MinDays);
        var messages = db.Messages.Where(m => m.AnalysisStatus == AnalysisStatus.Applied && !m.DeletedInGmail
            && !m.LabelIds.Contains(CleanUpQuery.TrashLabel) && m.InternalDate < newest);
        if (deleteLabelId is not null)
        {
            messages = messages.Where(m => !m.LabelIds.Contains(deleteLabelId));
        }

        if (ids is not null)
        {
            messages = messages.Where(m => ids.Contains(m.Id));
        }

        var rows = await (
            from m in messages
            join s in db.Suggestions on m.Id equals s.MessageId
            let rule = db.SenderPolicyRules.FirstOrDefault(r => r.Id == s.PolicyRuleId && r.Status == PolicyStatus.Approved)
            let policy = db.SenderPolicies.FirstOrDefault(p => p.Id == s.PolicyId && p.Status == PolicyStatus.Approved)
            select new
            {
                Message = new MessageRow
                {
                    Id = m.Id,
                    FromAddress = m.FromAddress,
                    Subject = m.Subject,
                    Snippet = m.Snippet,
                    InternalDate = m.InternalDate,
                    LabelIds = m.LabelIds,
                    HasAttachment = m.HasAttachment,
                    ThreadReplied = m.ThreadReplied,
                },
                s.MailType,
                RuleAction = rule == null ? (PolicyAction?)null : rule.Action,
                RuleDays = rule == null ? null : rule.RetentionDays,
                PolicyAction = policy == null ? (PolicyAction?)null : policy.Action,
                PolicyDays = policy == null ? null : policy.RetentionDays,
            })
            .AsNoTracking()
            .ToListAsync(ct);
        return [.. rows
            .Where(r => (r.RuleAction ?? r.PolicyAction) != PolicyAction.Keep
                && (r.RuleDays ?? r.PolicyDays ?? (r.MailType is { } type ? settings.Retention.DaysFor(type) : null)) is { } days
                && r.Message.InternalDate < now.AddDays(-days))
            .Select(r => r.Message)];
    }

    /// <summary>Records a label this run created on its batch, so undo can tell it apart.</summary>
    private Task RecordCreatedAsync(Guid batchId, GmailLabel label) =>
        db.Database.ExecuteSqlAsync(
            $"UPDATE action_batches SET created_label_ids = array_append(created_label_ids, {label.Id}) WHERE id = {batchId}",
            CancellationToken.None);

    /// <summary>
    /// Asks Gmail whether the user replied in the chunk's threads (stored on the rows); a replied one makes
    /// <see cref="PrepareAsync"/> replan without it. Outside the chunk's transaction: it calls Gmail.
    /// </summary>
    private async Task CheckThreadsAsync(string[] ids, CancellationToken ct)
    {
        if (!(await settingsStore.GetAsync(ct)).Protection.RepliedThreads)
        {
            return;
        }

        var messages = await db.Messages.AsNoTracking().Where(m => ids.Contains(m.Id) && m.ThreadReplied != true).ToListAsync(ct);
        await repliedThreads.CheckAsync(messages, ct);
    }

    /// <summary>
    /// The covered ids after <see cref="RetentionCursor.LastId"/>, not pending or skipped, in blocks of
    /// <see cref="BlockSize"/>; each block is one chunk per label change, the last carrying the block's highest id.
    /// </summary>
    private async Task<List<RetentionChunk>> ChunksAsync(RetentionCursor cursor, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var (covered, protectedCount) = await PlanAsync(db, guard, settings, cursor.DeleteLabelId, time.GetUtcNow(), ct);
        skippedProtected = protectedCount;
        var excluded = new HashSet<string>([.. cursor.Pending?.MessageIds ?? [], .. cursor.Skipped ?? []], StringComparer.Ordinal);
        var size = Math.Min(BlockSize, gmailOptions.Value.BatchModifyMaxIds);
        var chunks = new List<RetentionChunk>();
        foreach (var block in covered
            .Where(m => (cursor.LastId is null || string.CompareOrdinal(m.Id, cursor.LastId) > 0) && !excluded.Contains(m.Id))
            .Chunk(size))
        {
            var groups = LabelChunks.Group(block, m => Change(cursor.DeleteLabelId!, m).Add, m => Change(cursor.DeleteLabelId!, m).Remove, size).ToList();
            chunks.AddRange(groups.Select((g, i) => new RetentionChunk(
                [.. g.Items.Select(m => m.Id)], g.Add, g.Remove, i == groups.Count - 1 ? block[^1].Id : null)));
        }

        return chunks;
    }

    /// <summary>
    /// Locks the chunk's messages, re-checks each (still expired, unprotected, not transactional, same label change),
    /// writes the undo log and checkpoints the chunk as pending, in one transaction. Null when anything changed since
    /// planning: nothing was written, the caller replans.
    /// </summary>
    private async Task<RetentionCursor?> PrepareAsync(JobContext ctx, RetentionCursor cursor, RetentionChunk chunk, int total, CancellationToken ct)
    {
        var next = cursor with { Pending = chunk };
        var prepared = await Chunks.PrepareAsync(
            ctx, next, Progress(cursor, total), cursor.BatchId!.Value, chunk.MessageIds, chunk.Add, chunk.Remove, null, async (messages, t) =>
            {
                var settings = await settingsStore.GetAsync(t);
                var (covered, _) = await PlanAsync(db, guard, settings, cursor.DeleteLabelId, time.GetUtcNow(), t, chunk.MessageIds);
                var still = covered.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                return messages.All(m =>
                {
                    var (add, remove) = Change(cursor.DeleteLabelId!, m);
                    return still.Contains(m.Id)
                        && LabelChunks.Sorted(add).SequenceEqual(chunk.Add)
                        && LabelChunks.Sorted(remove).SequenceEqual(chunk.Remove);
                });
            }, ct);
        return prepared ? next : null;
    }

    /// <summary>
    /// Sends the pending chunk (<see cref="ChunkSender.SendAsync"/>), then stores its result, advances
    /// <see cref="RetentionCursor.LastId"/> when the chunk ends its block, and clears it.
    /// </summary>
    private async Task<(RetentionCursor Cursor, JobSignal Signal, int Total)> SendAsync(
        JobContext ctx, RetentionCursor cursor, bool resent, int total, CancellationToken ct)
    {
        var chunk = cursor.Pending!;
        var batchId = cursor.BatchId!.Value;
        var result = await Chunks.SendAsync(
            ctx, batchId, chunk.MessageIds, chunk.Add, chunk.Remove, resent, cursor with { Pending = null }, Progress(cursor, total), ct);
        var (changed, gone, rejected) = result;
        var done = cursor with
        {
            Pending = null,
            LastId = chunk.LastId is { } last && (cursor.LastId is null || string.CompareOrdinal(last, cursor.LastId) > 0) ? last : cursor.LastId,
            ChunksDone = cursor.ChunksDone + 1,
            MessagesDone = cursor.MessagesDone + changed.Length,
            Gone = cursor.Gone + gone.Length,
            Skipped = ChunkSender.Skipped(cursor.Skipped, result),
        };
        total -= rejected.Length;
        var signal = await Chunks.StoreAsync(
            ctx, done, Progress(done, total), batchId, chunk.Add, chunk.Remove, result, null,
            async t =>
            {
                if (gone.Length > 0)
                {
                    await senders.UpdateAsync(await db.Messages.Where(m => gone.Contains(m.Id)).Select(m => m.FromAddress).ToListAsync(t), t);
                }
            },
            ct);
        return (done, signal, total);
    }

    private static int Done(RetentionCursor cursor) => cursor.MessagesDone + cursor.Gone;

    /// <summary>Messages marked of the total, and every skip with its reason (protected first).</summary>
    private JobProgress Progress(RetentionCursor cursor, int total) =>
        new(Done(cursor), total,
            ChunkSender.Message("Marked", cursor.MessagesDone, total, skippedProtected, ProtectedReason, cursor.Gone, cursor.Skipped));
}
