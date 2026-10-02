using System.Text.Json.Serialization;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <param name="StartHistoryId">The profile's history ID taken before the first list call; #60 replays from it.</param>
/// <param name="AllMailTotal">The profile's message total minus Spam and Trash, which the listing excludes.</param>
public sealed record MailboxFetchCursor(
    [property: JsonConverter(typeof(JsonStringEnumConverter<MailboxPhase>))] MailboxPhase Phase,
    string? PageToken,
    int InboxFetched,
    int AllMailFetched,
    string StartHistoryId,
    long AllMailTotal);

/// <summary>
/// The full mailbox fetch (DESIGN §6.1): Inbox first, then All Mail, in chunks of <see cref="AppSettings.FetchChunkSize"/>,
/// checkpointing the Gmail page token after every chunk. Progress counts the messages processed in the current phase;
/// All Mail skips the <c>messages.get</c> of ids the Inbox phase of this run already stored.
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

        while (cursor.Phase is MailboxPhase.Inbox or MailboxPhase.AllMail)
        {
            var (next, progress) = await FetchNextChunkAsync(cursor, ct);
            cursor = next;
            if (cursor.Phase == MailboxPhase.Completed)
            {
                // One transaction: a failure before the commit leaves fetch_state, the run's ids and the job cursor
                // at the last All Mail checkpoint, so the re-run repeats only the final chunk.
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
            MailboxPhase.Inbox, null, 0, 0, profile.HistoryId, Math.Max(0, profile.MessagesTotal - excluded));
    }

    private async Task<(MailboxFetchCursor Cursor, JobProgress Progress)> FetchNextChunkAsync(
        MailboxFetchCursor cursor, CancellationToken ct)
    {
        var chunkSize = Math.Clamp(
            (await settings.GetAsync(ct)).FetchChunkSize, SettingsValidation.MinFetchChunkSize, SettingsValidation.MaxFetchChunkSize);
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
            var processed = chunk.Ids.Count - toFetch.Count + stored;
            cursor = cursor with { PageToken = chunk.NextPageToken, AllMailFetched = cursor.AllMailFetched + processed };
            progress = new JobProgress(cursor.AllMailFetched, done ? cursor.AllMailFetched : cursor.AllMailTotal, "Fetching All Mail");
        }

        if (done)
        {
            cursor = cursor with { Phase = inbox ? MailboxPhase.AllMail : MailboxPhase.Completed };
        }

        return (cursor, progress);
    }

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
