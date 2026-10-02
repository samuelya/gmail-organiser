using System.Text.Json.Serialization;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <param name="StartHistoryId">The profile's history ID taken before the first list call; #60 replays from it.</param>
/// <param name="AllMailTotal">The profile's message total minus Spam and Trash, which the listing excludes.</param>
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
    int Reconciled = 0);

/// <summary>
/// The full mailbox fetch (DESIGN §6.1): Inbox first, then All Mail, in chunks of <see cref="AppSettings.FetchChunkSize"/>,
/// checkpointing the Gmail page token after every chunk. Progress counts the messages processed in the current phase;
/// All Mail skips the <c>messages.get</c> of ids the Inbox phase of this run already stored. A resync then re-reads
/// the stored rows the listing missed: Gmail's 404 marks them deleted, a hit updates their labels (Spam, Trash).
/// </summary>
public sealed class MailboxFetchJob(
    IGmailClient gmail,
    MessageFetchPipeline pipeline,
    LocalAccountClaim accountClaim,
    ISettingsStore settings,
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
            if (await ctx.CheckpointAsync(cursor, new JobProgress(0, null, "Starting"), ct) != JobSignal.Continue)
            {
                return;
            }
        }

        while (cursor.Phase is MailboxPhase.Inbox or MailboxPhase.AllMail or MailboxPhase.Reconcile)
        {
            var (next, progress) = cursor.Phase == MailboxPhase.Reconcile
                ? await ReconcileNextChunkAsync(cursor, ct)
                : await FetchNextChunkAsync(cursor, ct);
            cursor = next;
            if (cursor.Phase == MailboxPhase.Completed)
            {
                // One transaction: a failure before the commit leaves fetch_state, the run's ids and the job cursor
                // at the last checkpoint, so the re-run repeats only the final chunk.
                await ctx.CompleteAsync(cursor, progress, c => SaveStateAsync(cursor, c), ct);
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
        var excluded = await gmail.GetLabelMessagesTotalAsync(SpamLabelId, ct) + await gmail.GetLabelMessagesTotalAsync(TrashLabelId, ct);
        var now = time.GetUtcNow();
        await db.FetchRunMessages.ExecuteDeleteAsync(ct);
        await db.FetchState.ExecuteUpdateAsync(set => set
            .SetProperty(f => f.MessagesTotal, profile.MessagesTotal)
            .SetProperty(f => f.MailboxPhase, MailboxPhase.Inbox)
            .SetProperty(f => f.PageToken, (string?)null)
            .SetProperty(f => f.InboxFetched, 0)
            .SetProperty(f => f.AllMailFetched, 0)
            .SetProperty(f => f.StartedAt, now)
            .SetProperty(f => f.CompletedAt, (DateTimeOffset?)null)
            .SetProperty(f => f.UpdatedAt, now), ct);
        return new MailboxFetchCursor(
            MailboxPhase.Inbox, null, 0, 0, profile.HistoryId, Math.Max(0, profile.MessagesTotal - excluded), resync);
    }

    private async Task<(MailboxFetchCursor Cursor, JobProgress Progress)> FetchNextChunkAsync(
        MailboxFetchCursor cursor, CancellationToken ct)
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

        var done = chunk.NextPageToken is null;
        JobProgress progress;
        if (inbox)
        {
            var stored = await pipeline.UpsertByIdsAsync(chunk.Ids, ct);
            await MarkStoredInRunAsync(chunk.Ids, ct);
            cursor = cursor with { PageToken = chunk.NextPageToken, InboxFetched = cursor.InboxFetched + stored };
            progress = new JobProgress(cursor.InboxFetched, done ? cursor.InboxFetched : chunk.ResultSizeEstimate, "Fetching Inbox");
        }
        else
        {
            var toFetch = await ExceptStoredInRunAsync(chunk.Ids, ct);
            var stored = await pipeline.UpsertByIdsAsync(toFetch, ct);
            if (cursor.Resync)
            {
                await MarkStoredInRunAsync(chunk.Ids, ct);
            }

            var processed = chunk.Ids.Count - toFetch.Count + stored;
            cursor = cursor with { PageToken = chunk.NextPageToken, AllMailFetched = cursor.AllMailFetched + processed };
            progress = new JobProgress(cursor.AllMailFetched, done ? cursor.AllMailFetched : cursor.AllMailTotal, "Fetching All Mail");
        }

        if (done)
        {
            var next = inbox ? MailboxPhase.AllMail : cursor.Resync ? MailboxPhase.Reconcile : MailboxPhase.Completed;
            cursor = cursor with { Phase = next };
        }

        return (cursor, progress);
    }

    /// <summary>
    /// Re-reads the next chunk of stored, undeleted rows this run did not list, in id order. Refreshing is idempotent,
    /// so replaying a chunk after a restart only repeats Gmail reads.
    /// </summary>
    private async Task<(MailboxFetchCursor Cursor, JobProgress Progress)> ReconcileNextChunkAsync(
        MailboxFetchCursor cursor, CancellationToken ct)
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
        await pipeline.RefreshByIdsAsync(ids, ct);
        var done = ids.Count < chunkSize;
        cursor = cursor with
        {
            Phase = done ? MailboxPhase.Completed : MailboxPhase.Reconcile,
            ReconcileAfter = ids.Count > 0 ? ids[^1] : cursor.ReconcileAfter,
            Reconciled = cursor.Reconciled + ids.Count,
        };
        return (cursor, new JobProgress(cursor.Reconciled, done ? cursor.Reconciled : null, "Reconciling stored mail"));
    }

    private async Task<int> ChunkSizeAsync(CancellationToken ct) => Math.Clamp(
        (await settings.GetAsync(ct)).FetchChunkSize, SettingsValidation.MinFetchChunkSize, SettingsValidation.MaxFetchChunkSize);

    private Task MarkStoredInRunAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"INSERT INTO fetch_run_messages (message_id) SELECT unnest({ids.ToArray()}) ON CONFLICT DO NOTHING", ct);

    private async Task<IReadOnlyList<string>> ExceptStoredInRunAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var seen = await db.FetchRunMessages
            .Where(r => ids.Contains(r.MessageId))
            .Select(r => r.MessageId)
            .ToHashSetAsync(StringComparer.Ordinal, ct);
        return [.. ids.Where(id => !seen.Contains(id))];
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
