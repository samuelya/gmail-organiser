using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <param name="StartHistoryId">The <c>fetch_state.last_history_id</c> this run replays from; every page uses it.</param>
/// <param name="PageToken">The next history page; null before the first page and after the last.</param>
/// <param name="NewHistoryId">The mailbox history ID reported by the last page applied.</param>
/// <param name="Touched">Messages re-read and stored so far.</param>
/// <param name="Deleted">Stored messages marked deleted so far.</param>
/// <param name="Replays">How often a rejected page token sent this run back to <paramref name="StartHistoryId"/>.</param>
public sealed record IncrementalFetchCursor(
    string StartHistoryId, string? PageToken, string? NewHistoryId, int Touched, int Deleted, int Replays = 0);

/// <summary>
/// The incremental fetch (DESIGN §6.1): applies Gmail's <c>history.list</c> since the last fetch, one page per
/// checkpoint. A page is applied as state, not as deltas: deleted ids are marked deleted and every other touched id is
/// re-read and upserted (which is how removed labels show), so re-applying a page changes nothing.
/// <c>fetch_state.last_history_id</c> moves only on completion. Expired history resets the mailbox phase on completion
/// and then queues a full resync, which reconciles the stored mail it does not list. After each page the re-read
/// messages go to <see cref="IActionDoneScanner"/> (auto-archive). The account guard runs after every Gmail read and
/// before the writes that follow it, so a reconnect to another account mid-page stores nothing of that page.
/// </summary>
public sealed partial class IncrementalFetchJob(
    IGmailClient gmail,
    MessageFetchPipeline pipeline,
    IActionDoneScanner actionDone,
    LocalAccountClaim accountClaim,
    IJobService jobs,
    AppDbContext db,
    TimeProvider time,
    ILogger<IncrementalFetchJob> logger) : IJobHandler
{
    public const string JobType = FetchJobTypes.Incremental;
    public const string Queue = JobQueues.Fetch;
    public const string ExpiredMessage = "history expired, full resync queued";
    private const string ApplyingMessage = "Applying Gmail history";
    private const int MaxReplays = 2;

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<IncrementalFetchCursor>();
        if (cursor is null)
        {
            var state = await db.FetchState.AsNoTracking().SingleAsync(s => s.Id == FetchStateRow.SingletonId, ct);
            if (state.MailboxPhase != MailboxPhase.Completed || state.LastHistoryId is null)
            {
                // The endpoint checks this too; a full fetch may have been restarted since the job was queued.
                await ctx.CompleteAsync<IncrementalFetchCursor?>(null, new JobProgress(0, 0, "Run the mailbox fetch first"), _ => Task.CompletedTask, ct);
                return;
            }

            cursor = new IncrementalFetchCursor(state.LastHistoryId, null, null, 0, 0);
        }

        await accountClaim.ClaimAsync((await gmail.GetProfileAsync(ct)).EmailAddress, ct);

        while (true)
        {
            HistoryPage page;
            try
            {
                page = await gmail.ListHistoryAsync(cursor.StartHistoryId, cursor.PageToken, ct);
            }
            catch (GmailHistoryExpiredException ex)
            {
                LogHistoryExpired(logger, ex);
                await ctx.CompleteAsync(cursor, new JobProgress(0, 0, ExpiredMessage), ResetMailboxPhaseAsync, ct);

                // After the commit: enqueueing inside it would publish an uncommitted job and break JobService's
                // unique-violation retry. A crash in between leaves the phase reset, so the next Start runs a full fetch.
                await jobs.EnqueueAsync(MailboxFetchJob.JobType, MailboxFetchJob.Queue, null, ct);
                return;
            }
            catch (GmailInvalidPageTokenException ex) when (cursor.PageToken is not null)
            {
                // Pages are idempotent, so replaying from the start id is safe; only the work (and its count) is repeated.
                if (cursor.Replays >= MaxReplays)
                {
                    throw new InvalidOperationException("Gmail kept rejecting history page tokens it had just issued.", ex);
                }

                LogPageTokenRejected(logger, ex);
                cursor = cursor with { PageToken = null, Touched = 0, Deleted = 0, Replays = cursor.Replays + 1 };
                continue;
            }

            cursor = await ApplyAsync(ctx, cursor, page, ct);
            var progress = new JobProgress(cursor.Touched + cursor.Deleted, null, ApplyingMessage);
            if (cursor.PageToken is null)
            {
                var newHistoryId = cursor.NewHistoryId!;
                var total = cursor.Touched + cursor.Deleted;
                await ctx.CompleteAsync(cursor, progress with { Total = total }, c => SaveHistoryIdAsync(newHistoryId, c), ct);
                await SenderStatsRebuildJob.EnqueueAsync(jobs, ct);
                return;
            }

            if (await ctx.CheckpointAsync(cursor, progress, ct) != JobSignal.Continue)
            {
                return;
            }
        }
    }

    private async Task<IncrementalFetchCursor> ApplyAsync(
        JobContext ctx, IncrementalFetchCursor cursor, HistoryPage page, CancellationToken ct)
    {
        await ctx.EnsureMayWriteAsync(ct);
        var deleted = page.Records.SelectMany(r => r.MessagesDeleted).ToHashSet(StringComparer.Ordinal);
        var touched = page.Records
            .SelectMany(r => r.MessagesAdded
                .Concat(r.LabelsAdded.Select(c => c.MessageId))
                .Concat(r.LabelsRemoved.Select(c => c.MessageId)))
            .Where(id => !deleted.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The sets are disjoint, so the re-read (and its guard) goes first: the page's first write follows the last guard.
        var refreshed = await pipeline.RefreshByIdsAsync(touched, ctx.EnsureMayWriteAsync, ct);
        var marked = await pipeline.MarkDeletedAsync([.. deleted], ct);
        await ctx.EnsureMayWriteAsync(ct);
        await actionDone.ScanAsync(touched, ct);
        return cursor with
        {
            PageToken = page.NextPageToken,
            NewHistoryId = page.HistoryId,
            Touched = cursor.Touched + refreshed.Stored,
            Deleted = cursor.Deleted + marked + refreshed.Deleted,
        };
    }

    private Task SaveHistoryIdAsync(string historyId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.FetchState.ExecuteUpdateAsync(set => set
            .SetProperty(f => f.LastHistoryId, historyId)
            .SetProperty(f => f.UpdatedAt, now), ct);
    }

    /// <summary>Makes the next Start a full mailbox fetch; finding stored mail, it reconciles what it does not list.</summary>
    private Task ResetMailboxPhaseAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.FetchState.ExecuteUpdateAsync(set => set
            .SetProperty(f => f.MailboxPhase, MailboxPhase.NotStarted)
            .SetProperty(f => f.PageToken, (string?)null)
            .SetProperty(f => f.UpdatedAt, now), ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail history expired; queued a full mailbox resync")]
    private static partial void LogHistoryExpired(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail rejected the stored history page token; replaying from the start history ID")]
    private static partial void LogPageTokenRejected(ILogger logger, Exception ex);
}
