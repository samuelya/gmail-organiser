using System.Text.Json.Serialization;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <param name="StartHistoryId">The profile's history ID taken before the first list call; #60 replays from it.</param>
/// <param name="AllMailTotal">The profile's message total minus Spam and Trash, which the listing excludes.</param>
/// <param name="InboxTotal">The Inbox label's message total (<c>labels.get</c>); null in a cursor saved before it existed,
/// which a run resumed in the Inbox phase reads again.</param>
/// <param name="Resync">Mail was stored when the run started, so the run records every listed id and then reconciles
/// the stored rows it did not list (a resync after expired history, or a restarted fetch).</param>
/// <param name="ReconcileAfter">The last stored id reconciled; the reconcile phase resumes after it.</param>
/// <param name="Reconciled">Stored rows re-read by the reconcile phase so far.</param>
public sealed record MailboxFetchCursor(
    [property: JsonConverter(typeof(JsonStringEnumConverter<MailboxPhase>))] MailboxPhase Phase,
    string? PageToken,
    int InboxFetched,
    int AllMailFetched,
    string StartHistoryId,
    long AllMailTotal,
    bool Resync = false,
    string? ReconcileAfter = null,
    int Reconciled = 0,
    long? InboxTotal = null);

/// <summary>
/// The full mailbox fetch (DESIGN §6.1): Inbox first, then All Mail, in chunks of <see cref="AppSettings.FetchChunkSize"/>,
/// checkpointing the Gmail page token after every chunk. Progress counts the messages handled in the current phase.
/// Both phases skip ids this run already handled, refresh only the labels (<c>format=minimal</c>) of ids already stored
/// by any earlier fetch, and fetch full metadata only for new ids. A resync then re-reads the labels of the stored rows
/// the listing missed: Gmail's 404 marks them deleted, a hit updates their labels (Spam, Trash).
/// </summary>
public sealed class MailboxFetchJob(
    IGmailClient gmail,
    MessageFetchPipeline pipeline,
    LocalAccountClaim accountClaim,
    ISettingsStore settings,
    MailboxTotalsReader totalsReader,
    AppDbContext db,
    TimeProvider time) : IJobHandler
{
    public const string JobType = FetchJobTypes.Mailbox;
    public const string Queue = JobQueues.Fetch;
    public const string InboxLabelId = "INBOX";
    public const string SpamLabelId = "SPAM";
    public const string TrashLabelId = "TRASH";

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<MailboxFetchCursor>();
        if (cursor is null)
        {
            cursor = await StartAsync(ct);
            if (await ctx.CheckpointAsync(cursor, new JobProgress(0, cursor.InboxTotal, "Starting"), ct) != JobSignal.Continue)
            {
                return;
            }
        }
        else if (cursor.InboxTotal is null && cursor.Phase == MailboxPhase.Inbox)
        {
            cursor = cursor with { InboxTotal = await gmail.GetLabelMessagesTotalAsync(InboxLabelId, ct) };
        }

        while (cursor.Phase is MailboxPhase.Inbox or MailboxPhase.AllMail or MailboxPhase.Reconcile)
        {
            var (next, progress) = cursor.Phase == MailboxPhase.Reconcile
                ? await ReconcileNextChunkAsync(cursor, ctx, ct)
                : await FetchNextChunkAsync(cursor, ctx, ct);
            cursor = next;
            if (cursor.Phase == MailboxPhase.Completed)
            {
                // One transaction: a failure before the commit leaves fetch_state, the run's ids and the job cursor
                // at the last checkpoint, so the re-run repeats only the final chunk. The stats rebuild is queued in it too.
                await ctx.CompleteAsync(
                    cursor,
                    progress,
                    async c =>
                    {
                        await SaveStateAsync(cursor, c);
                        await SenderStatsRebuildJob.EnqueueAsync(db, time, null, c);
                    },
                    ct);
                return;
            }

            await SaveStateAsync(cursor, ct);
            if (await ctx.CheckpointAsync(cursor, progress, ct) != JobSignal.Continue)
            {
                return;
            }
        }
    }

    /// <summary>Takes the history ID first, so anything that changes during the pass is replayed from it later.</summary>
    private async Task<MailboxFetchCursor> StartAsync(CancellationToken ct)
    {
        var resync = await db.Messages.AnyAsync(ct);
        var profile = await gmail.GetProfileAsync(ct);
        await accountClaim.ClaimAsync(profile.EmailAddress, ct);
        var totals = await totalsReader.MeasureAsync(gmail, profile, ct);
        var inboxTotal = totals.Inbox;
        var allMailTotal = totals.AllMail;
        var now = time.GetUtcNow();
        await db.FetchRunMessages.ExecuteDeleteAsync(ct);
        await db.FetchState.ExecuteUpdateAsync(set => set
            .SetProperty(f => f.MessagesTotal, profile.MessagesTotal)
            .SetProperty(f => f.MailboxPhase, MailboxPhase.Inbox)
            .SetProperty(f => f.PageToken, (string?)null)
            .SetProperty(f => f.InboxFetched, 0)
            .SetProperty(f => f.AllMailFetched, 0)
            .SetProperty(f => f.InboxTotal, inboxTotal)
            .SetProperty(f => f.AllMailTotal, allMailTotal)
            .SetProperty(f => f.StartedAt, now)
            .SetProperty(f => f.CompletedAt, (DateTimeOffset?)null)
            .SetProperty(f => f.UpdatedAt, now), ct);
        return new MailboxFetchCursor(
            MailboxPhase.Inbox, null, 0, 0, profile.HistoryId, allMailTotal, resync, InboxTotal: inboxTotal);
    }

    private async Task<(MailboxFetchCursor Cursor, JobProgress Progress)> FetchNextChunkAsync(
        MailboxFetchCursor cursor, JobContext ctx, CancellationToken ct)
    {
        var chunkSize = await ChunkSizeAsync(ct);
        var inbox = cursor.Phase == MailboxPhase.Inbox;
        var query = new MessageListQuery(null, inbox ? [InboxLabelId] : null, cursor.PageToken, MessageListQuery.MaxPageSize);

        var chunk = await pipeline.ListChunkAsync(query, chunkSize, ct);
        if (chunk.Restarted)
        {
            // The pipeline listed the phase from its first page again; only the phase's counter starts over.
            cursor = inbox ? cursor with { InboxFetched = 0 } : cursor with { AllMailFetched = 0 };
        }

        var processed = await StoreChunkAsync(chunk.Ids, ctx, ct);
        cursor = inbox
            ? cursor with { PageToken = chunk.NextPageToken, InboxFetched = cursor.InboxFetched + processed }
            : cursor with { PageToken = chunk.NextPageToken, AllMailFetched = cursor.AllMailFetched + processed };

        if (chunk.NextPageToken is null)
        {
            var next = inbox ? MailboxPhase.AllMail : cursor.Resync ? MailboxPhase.Reconcile : MailboxPhase.Completed;
            cursor = cursor with { Phase = next };
        }

        var progress = inbox
            ? new JobProgress(cursor.InboxFetched, InboxTotalOf(cursor), "Fetching Inbox")
            : new JobProgress(cursor.AllMailFetched, AllMailTotalOf(cursor), "Fetching All Mail");
        return (cursor, progress);
    }

    /// <summary>
    /// Re-reads the next chunk of stored, undeleted rows this run did not list, in id order. Refreshing is idempotent,
    /// so replaying a chunk after a restart only repeats Gmail reads.
    /// </summary>
    private async Task<(MailboxFetchCursor Cursor, JobProgress Progress)> ReconcileNextChunkAsync(
        MailboxFetchCursor cursor, JobContext ctx, CancellationToken ct)
    {
        if (cursor.ReconcileAfter is null)
        {
            // The phase's first write: a resumed run may reach it without passing StartAsync's claim.
            await accountClaim.ClaimAsync((await gmail.GetProfileAsync(ct)).EmailAddress, ct);
        }

        var chunkSize = await ChunkSizeAsync(ct);
        var unseen = db.Messages.AsNoTracking()
            .Where(m => !m.DeletedInGmail && !db.FetchRunMessages.Any(r => r.MessageId == m.Id));
        if (cursor.ReconcileAfter is { } after)
        {
            unseen = unseen.Where(m => string.Compare(m.Id, after) > 0);
        }

        var ids = await unseen.OrderBy(m => m.Id).Select(m => m.Id).Take(chunkSize).ToListAsync(ct);
        await pipeline.RefreshLabelsByIdsAsync(ids, ctx.EnsureMayWriteAsync, ct);
        var done = ids.Count < chunkSize;
        cursor = cursor with
        {
            Phase = done ? MailboxPhase.Completed : MailboxPhase.Reconcile,
            ReconcileAfter = ids.Count > 0 ? ids[^1] : cursor.ReconcileAfter,
            Reconciled = cursor.Reconciled + ids.Count,
        };
        return (cursor, new JobProgress(cursor.Reconciled, done ? cursor.Reconciled : null, "Reconciling stored mail"));
    }

    /// <summary>
    /// The phase totals that progress reports: a finished phase reports what it fetched, a running
    /// one at least that (mail arriving during the run can push the count past the start total; the bar never overflows).
    /// </summary>
    private static long InboxTotalOf(MailboxFetchCursor cursor) =>
        cursor.Phase == MailboxPhase.Inbox ? Math.Max(cursor.InboxTotal ?? 0, cursor.InboxFetched) : cursor.InboxFetched;

    /// <inheritdoc cref="InboxTotalOf"/>
    private static long AllMailTotalOf(MailboxFetchCursor cursor) =>
        cursor.Phase is MailboxPhase.Inbox or MailboxPhase.AllMail
            ? Math.Max(cursor.AllMailTotal, cursor.AllMailFetched)
            : cursor.AllMailFetched;

    private async Task<int> ChunkSizeAsync(CancellationToken ct) => Math.Clamp(
        (await settings.GetAsync(ct)).FetchChunkSize, SettingsValidation.MinFetchChunkSize, SettingsValidation.MaxFetchChunkSize);

    private Task MarkStoredInRunAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"INSERT INTO fetch_run_messages (message_id) SELECT unnest({ids.ToArray()}) ON CONFLICT DO NOTHING", ct);

    /// <summary>
    /// Skips ids this run already handled, refreshes only the labels of ids already stored and fetches full metadata for
    /// the rest, then records every listed id. A chunk replayed after a crash finds its new ids stored, so it only repeats reads.
    /// The run guard is asked after each Gmail read and before its writes, so a reconnect mid-chunk stores nothing of it.
    /// </summary>
    /// <returns>The listed ids handled: skipped, refreshed or stored (ids Gmail no longer knows are not counted).</returns>
    private async Task<int> StoreChunkAsync(IReadOnlyList<string> ids, JobContext ctx, CancellationToken ct)
    {
        var seen = await db.FetchRunMessages
            .Where(r => ids.Contains(r.MessageId))
            .Select(r => r.MessageId)
            .ToHashSetAsync(StringComparer.Ordinal, ct);
        var unseen = ids.Where(id => !seen.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
        var stored = await db.Messages
            .Where(m => unseen.Contains(m.Id))
            .Select(m => m.Id)
            .ToHashSetAsync(StringComparer.Ordinal, ct);

        var refreshed = await pipeline.RefreshLabelsByIdsAsync([.. unseen.Where(stored.Contains)], ctx.EnsureMayWriteAsync, ct);
        var fetched = await pipeline.UpsertByIdsAsync([.. unseen.Where(id => !stored.Contains(id))], ctx.EnsureMayWriteAsync, ct);
        await MarkStoredInRunAsync(ids, ct);
        return ids.Count - unseen.Count + refreshed.Stored + fetched;
    }

    /// <summary>Mirrors the cursor to <c>fetch_state</c>; a completed fetch also records where #60 replays from.</summary>
    private async Task SaveStateAsync(MailboxFetchCursor cursor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var completed = cursor.Phase == MailboxPhase.Completed;
        await db.FetchState.ExecuteUpdateAsync(set =>
        {
            set.SetProperty(f => f.MailboxPhase, cursor.Phase)
                .SetProperty(f => f.PageToken, cursor.PageToken)
                .SetProperty(f => f.InboxFetched, cursor.InboxFetched)
                .SetProperty(f => f.AllMailFetched, cursor.AllMailFetched)
                .SetProperty(f => f.UpdatedAt, now);
            if (completed)
            {
                set.SetProperty(f => f.LastHistoryId, cursor.StartHistoryId)
                    .SetProperty(f => f.CompletedAt, now);
            }
        }, ct);
        if (completed)
        {
            await db.FetchRunMessages.ExecuteDeleteAsync(ct);
        }
    }
}
