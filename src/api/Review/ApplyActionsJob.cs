using System.Net;
using System.Text.RegularExpressions;
using Google;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Review;

/// <param name="ApprovedBefore">Only suggestions approved at or before this (the batch's creation) are applied.</param>
/// <param name="Total">The approved suggestions counted when the batch was created.</param>
/// <param name="SuggestionIds">Null for every approved suggestion (of <paramref name="SenderAddress"/>).</param>
/// <param name="Pending">The chunk whose undo log is written and whose <c>batchModify</c> may not have been sent yet.</param>
public sealed record ApplyCursor(
    Guid BatchId,
    DateTimeOffset ApprovedBefore,
    int Total,
    string? SenderAddress = null,
    Guid[]? SuggestionIds = null,
    int ChunksDone = 0,
    int MessagesDone = 0,
    ApplyChunk? Pending = null);

/// <summary>One <c>batchModify</c> call: the same label ids added and removed on every message.</summary>
public sealed record ApplyChunk(string[] MessageIds, string[] Add, string[] Remove);

/// <summary>
/// Applies approved suggestions to Gmail (DESIGN §6.3). Messages with the same label changes are sent together in
/// chunks of at most <see cref="GmailOptions.BatchModifyMaxIds"/>. Each chunk is two transactions around the Gmail
/// call: the first locks the suggestions, writes the undo log, marks them applied and checkpoints the chunk as
/// pending; the second updates the stored labels and counts and clears it. A restart re-sends a pending chunk
/// (<c>batchModify</c> is idempotent); a Gmail refusal that changed nothing reverts it.
/// </summary>
public sealed partial class ApplyActionsJob(
    AppDbContext db,
    IGmailClient gmail,
    LabelResolver labels,
    ISettingsStore settingsStore,
    IOptions<GmailOptions> gmailOptions,
    TimeProvider time,
    ILogger<ApplyActionsJob> logger) : IJobHandler
{
    public const string JobType = ReviewJobTypes.Apply;
    public const string Queue = JobQueues.Apply;

    public string Type => JobType;

    /// <summary>The suggestions a batch applies: approved by <paramref name="approvedBefore"/>, message not deleted in Gmail.</summary>
    public static IQueryable<SuggestionRow> Eligible(
        AppDbContext db, DateTimeOffset approvedBefore, string? senderAddress, Guid[]? suggestionIds)
    {
        var query = db.Suggestions.Where(s => s.Status == SuggestionStatus.Approved
            && s.DecidedAt <= approvedBefore
            && db.Messages.Any(m => m.Id == s.MessageId && !m.DeletedInGmail));
        if (senderAddress is not null)
        {
            query = query.Where(s => s.SenderAddress == senderAddress);
        }

        if (suggestionIds is not null)
        {
            query = query.Where(s => suggestionIds.Contains(s.Id));
        }

        return query;
    }

    public static string Describe(int count, string? senderAddress) =>
        $"Apply {count} suggestion{(count == 1 ? "" : "s")}{(senderAddress is null ? "" : $" for {senderAddress}")}";

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<ApplyCursor>() ?? throw new JobRefusedException("The apply job has no batch.");
        if (!await db.ActionBatches.AnyAsync(b => b.Id == cursor.BatchId, ct))
        {
            throw new JobRefusedException("The action batch no longer exists.");
        }

        try
        {
            await RunCoreAsync(ctx, cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            await MarkPartialAsync(ctx.ReadCursor<ApplyCursor>() ?? cursor);
            throw;
        }
    }

    private async Task RunCoreAsync(JobContext ctx, ApplyCursor cursor, CancellationToken ct)
    {
        if (cursor.Pending is { } resumed)
        {
            (cursor, var signal) = await SendAsync(ctx, cursor, cursor.MessagesDone + resumed.MessageIds.Length, ct);
            if (await StoppedAsync(signal, cursor))
            {
                return;
            }
        }

        var settings = await settingsStore.GetAsync(ct);
        while (true)
        {
            var plan = await PlanAsync(cursor, settings, ct);
            var total = cursor.MessagesDone + plan.Chunks.Sum(c => c.Items.Count);
            var replan = false;
            foreach (var chunk in plan.Chunks)
            {
                if (await PrepareAsync(ctx, cursor, chunk, plan.Names, total, ct) is not { } prepared)
                {
                    replan = true;
                    break;
                }

                (cursor, var signal) = await SendAsync(ctx, prepared, total, ct);
                if (await StoppedAsync(signal, cursor))
                {
                    return;
                }
            }

            if (!replan)
            {
                var progress = Progress(cursor.MessagesDone, total, plan.Skipped);
                await ctx.CompleteAsync(cursor, progress, t => SetDescriptionAsync(cursor.BatchId, null, t), ct);
                return;
            }
        }
    }

    /// <summary>Plans every eligible suggestion; topic labels Gmail would refuse are skipped and stay approved.</summary>
    private async Task<Plan> PlanAsync(ApplyCursor cursor, AppSettings settings, CancellationToken ct)
    {
        var rows = await Eligible(db, cursor.ApprovedBefore, cursor.SenderAddress, cursor.SuggestionIds)
            .AsNoTracking()
            .Join(db.Messages.AsNoTracking(), s => s.MessageId, m => m.Id, (s, m) => new { Suggestion = s, Message = m })
            .OrderBy(x => x.Suggestion.Id)
            .ToListAsync(ct);
        var allowlisted = (await db.Senders.Where(s => s.Allowlisted).Select(s => s.Address).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var valid = rows.Where(r => LabelResolver.IsValid(r.Suggestion.TopicLabel)).ToList();
        var skipped = rows.Count - valid.Count;

        var paths = valid.Select(r => r.Suggestion.TopicLabel).ToList();
        if (valid.Any(r => r.Suggestion.NeedsAction))
        {
            paths.Add(SettingLabel(settings.ActionLabelName, nameof(AppSettings.ActionLabelName)));
        }

        if (valid.Any(r => r.Suggestion.ToBeDeleted
            && ActionPlanner.ProtectionReason(r.Message, allowlisted.Contains(r.Message.FromAddress)) is null))
        {
            paths.Add(SettingLabel(settings.DeleteLabelName, nameof(AppSettings.DeleteLabelName)));
        }

        var labelIds = valid.Count == 0 ? new Dictionary<string, string>() : await labels.EnsureAsync(paths, ct);
        var cap = gmailOptions.Value.BatchModifyMaxIds;
        var chunks = valid
            .Select(r => new PlannedItem(
                r.Suggestion, r.Message,
                ActionPlanner.Plan(r.Suggestion, r.Message, labelIds, settings, allowlisted.Contains(r.Message.FromAddress))))
            .GroupBy(i => (Add: Key(i.Plan.Add), Remove: Key(i.Plan.Remove)))
            .SelectMany(g => g.Chunk(cap).Select(items => new PlannedChunk(
                items, [.. g.First().Plan.Add.Order(StringComparer.Ordinal)], [.. g.First().Plan.Remove.Order(StringComparer.Ordinal)])))
            .ToList();
        return new Plan(chunks, skipped, labelIds.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal));

        static string Key(IReadOnlyList<string> ids) => string.Join('\n', ids.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Locks the chunk's suggestions, writes the undo log, marks them applied and checkpoints the chunk as pending,
    /// in one transaction. Null when a suggestion changed since planning: nothing was written, the caller replans.
    /// </summary>
    private async Task<ApplyCursor?> PrepareAsync(
        JobContext ctx, ApplyCursor cursor, PlannedChunk chunk, IReadOnlyDictionary<string, string> names, int total, CancellationToken ct)
    {
        var ids = chunk.Items.Select(i => i.Suggestion.Id).ToArray();
        var next = cursor with { Pending = new ApplyChunk([.. chunk.Items.Select(i => i.Message.Id)], chunk.Add, chunk.Remove) };
        try
        {
            await ctx.CheckpointAsync(next, Progress(cursor.MessagesDone, total, 0), async t =>
            {
                // Same lock order as the review endpoints, so the two never deadlock.
                var locked = await db.Suggestions
                    .FromSql($"SELECT * FROM suggestions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
                    .ToListAsync(t);
                if (locked.Count != ids.Length
                    || locked.Any(s => s.Status != SuggestionStatus.Approved || s.DecidedAt > cursor.ApprovedBefore))
                {
                    throw new PlanChangedException();
                }

                var messageIds = locked.ConvertAll(s => s.MessageId);
                var messages = await db.Messages.Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, t);
                var notes = chunk.Items.ToDictionary(i => i.Suggestion.Id, i => i.Plan.Note);
                var now = time.GetUtcNow();
                foreach (var suggestion in locked)
                {
                    var message = messages[suggestion.MessageId];
                    db.ActionLog.Add(new ActionLogRow
                    {
                        Id = Guid.CreateVersion7(now),
                        BatchId = cursor.BatchId,
                        MessageId = message.Id,
                        SuggestionId = suggestion.Id,
                        LabelsAdded = [.. chunk.Add.Select(id => names.GetValueOrDefault(id, id))],
                        LabelsRemoved = [.. chunk.Remove.Select(id => names.GetValueOrDefault(id, id))],
                        LabelIdsBefore = message.LabelIds,
                        LabelIdsAfter = After(message.LabelIds, chunk.Add, chunk.Remove),
                        Note = notes[suggestion.Id],
                        CreatedAt = now,
                    });
                    suggestion.SetStatus(SuggestionStatus.Applied, message, now);
                }

                await db.SaveChangesAsync(t);
            }, ct);
        }
        catch (PlanChangedException)
        {
            db.ChangeTracker.Clear();
            return null;
        }

        db.ChangeTracker.Clear();
        return next;
    }

    /// <summary>Sends the pending chunk, then stores its result and clears it; reverts it when Gmail certainly refused it.</summary>
    private async Task<(ApplyCursor Cursor, JobSignal Signal)> SendAsync(JobContext ctx, ApplyCursor cursor, int total, CancellationToken ct)
    {
        var pending = cursor.Pending!;
        if (pending.Add.Length + pending.Remove.Length > 0)
        {
            try
            {
                await gmail.BatchModifyAsync(pending.MessageIds, pending.Add, pending.Remove, ct);
            }
            catch (Exception ex) when (NothingChanged(ex))
            {
                LogChunkRefused(logger, pending.MessageIds.Length, ex);
                await RevertAsync(ctx, cursor, total);
                throw;
            }
        }

        var done = cursor with
        {
            Pending = null,
            ChunksDone = cursor.ChunksDone + 1,
            MessagesDone = cursor.MessagesDone + pending.MessageIds.Length,
        };
        var signal = await ctx.CheckpointAsync(done, Progress(done.MessagesDone, total, 0), async t =>
        {
            var now = time.GetUtcNow();
            var messages = await db.Messages.Where(m => pending.MessageIds.Contains(m.Id)).ToListAsync(t);
            foreach (var message in messages)
            {
                message.LabelIds = After(message.LabelIds, pending.Add, pending.Remove);
                message.UpdatedAt = now;
            }

            await db.SaveChangesAsync(t);
            foreach (var sender in messages.GroupBy(m => m.FromAddress))
            {
                var count = sender.Count();
                await db.Senders.Where(s => s.Address == sender.Key)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.AppliedCount, r => r.AppliedCount + count), t);
            }

            await db.ActionBatches.Where(b => b.Id == cursor.BatchId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.MessageCount, b => b.MessageCount + pending.MessageIds.Length), t);
        }, ct);
        db.ChangeTracker.Clear();
        return (done, signal);
    }

    /// <summary>Undoes <see cref="PrepareAsync"/> for a chunk Gmail never applied: log rows deleted, suggestions approved again.</summary>
    private async Task RevertAsync(JobContext ctx, ApplyCursor cursor, int total)
    {
        db.ChangeTracker.Clear();
        var pending = cursor.Pending!;
        await ctx.CheckpointAsync(cursor with { Pending = null }, Progress(cursor.MessagesDone, total, 0), async t =>
        {
            var log = await db.ActionLog
                .Where(l => l.BatchId == cursor.BatchId && pending.MessageIds.Contains(l.MessageId))
                .ToListAsync(t);
            var suggestionIds = log.Where(l => l.SuggestionId is not null).Select(l => l.SuggestionId!.Value).ToList();
            var suggestions = await db.Suggestions
                .Where(s => suggestionIds.Contains(s.Id) && s.Status == SuggestionStatus.Applied)
                .ToListAsync(t);
            var messages = await db.Messages.Where(m => pending.MessageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, t);
            foreach (var suggestion in suggestions)
            {
                // Keeps the approval time, so the batch still covers the suggestion on resume.
                suggestion.SetStatus(SuggestionStatus.Approved, messages[suggestion.MessageId], suggestion.DecidedAt ?? time.GetUtcNow());
            }

            db.ActionLog.RemoveRange(log);
            await db.SaveChangesAsync(t);
        }, CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    /// <summary>A cancelled run marks the batch partial; a pause leaves it for the resume.</summary>
    private async Task<bool> StoppedAsync(JobSignal signal, ApplyCursor cursor)
    {
        if (signal == JobSignal.Cancel)
        {
            await MarkPartialAsync(cursor);
        }

        return signal != JobSignal.Continue;
    }

    private async Task MarkPartialAsync(ApplyCursor cursor)
    {
        try
        {
            await SetDescriptionAsync(cursor.BatchId, $" (partial: {cursor.MessagesDone} of {cursor.Total})", CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogPartialNotRecorded(logger, ex);
        }
    }

    /// <summary>The batch's description without any earlier partial suffix, plus <paramref name="suffix"/>.</summary>
    private async Task SetDescriptionAsync(Guid batchId, string? suffix, CancellationToken ct)
    {
        var batch = await db.ActionBatches.SingleAsync(b => b.Id == batchId, ct);
        batch.Description = PartialSuffix().Replace(batch.Description, "") + suffix;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Gmail refused the call before changing anything: rate-limited on every attempt, not connected, or a 4xx.</summary>
    private static bool NothingChanged(Exception ex) => ex
        is GmailRateLimitedException
        or GmailNotConnectedException
        or ArgumentException
        or GoogleApiException { HttpStatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError };

    private static string[] After(string[] before, string[] add, string[] remove) =>
        [.. before.Except(remove, StringComparer.Ordinal).Concat(add.Except(before, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)];

    private static JobProgress Progress(int done, int total, int skipped) => new(
        done, total, $"Applied {done} of {total} messages{(skipped > 0 ? $"; {skipped} skipped (invalid label)" : "")}");

    private static string SettingLabel(string path, string setting) =>
        LabelResolver.IsValid(path) ? path : throw new JobRefusedException($"The {setting} setting is not a valid Gmail label.");

    [GeneratedRegex(@" \(partial: \d+ of \d+\)$")]
    private static partial Regex PartialSuffix();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail refused a chunk of {Count} messages; the chunk was reverted.")]
    private static partial void LogChunkRefused(ILogger logger, int count, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not mark the action batch as partial.")]
    private static partial void LogPartialNotRecorded(ILogger logger, Exception exception);

    private sealed record PlannedItem(SuggestionRow Suggestion, MessageRow Message, ActionPlan Plan);

    private sealed record PlannedChunk(IReadOnlyList<PlannedItem> Items, string[] Add, string[] Remove);

    /// <param name="Names">Label id to the path it was resolved from, for the log's display names.</param>
    private sealed record Plan(IReadOnlyList<PlannedChunk> Chunks, int Skipped, IReadOnlyDictionary<string, string> Names);

    private sealed class PlanChangedException : Exception;
}
