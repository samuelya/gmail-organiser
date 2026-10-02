using System.Text.Json.Serialization;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

/// <param name="StartHistoryId">The profile's history ID taken before the first list call; #60 replays from it.</param>
public sealed record MailboxFetchCursor(
    [property: JsonConverter(typeof(JsonStringEnumConverter<MailboxPhase>))] MailboxPhase Phase,
    string? PageToken,
    int InboxFetched,
    int AllMailFetched,
    string StartHistoryId);

/// <summary>
/// The full mailbox fetch (DESIGN §6.1): Inbox first, then All Mail, in chunks of <see cref="AppSettings.FetchChunkSize"/>,
/// checkpointing the Gmail page token after every chunk. Progress counts the messages stored in the current phase.
/// </summary>
public sealed partial class MailboxFetchJob(
    IGmailClient gmail,
    MessageFetchPipeline pipeline,
    ISettingsStore settings,
    AppDbContext db,
    TimeProvider time,
    ILogger<MailboxFetchJob> logger) : IJobHandler
{
    public const string JobType = "mailbox_fetch";
    public const string Queue = JobQueues.Fetch;
    public const string InboxLabelId = "INBOX";

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<MailboxFetchCursor>();
        long? messagesTotal = null;
        if (cursor is null)
        {
            (cursor, messagesTotal) = await StartAsync(ct);
            if (await ctx.CheckpointAsync(cursor, new JobProgress(0, null, "Starting"), ct) != JobSignal.Continue)
            {
                return;
            }
        }

        messagesTotal ??= await db.FetchState.Select(f => f.MessagesTotal).SingleAsync(ct);
        while (cursor.Phase is MailboxPhase.Inbox or MailboxPhase.AllMail)
        {
            var (next, progress) = await FetchNextChunkAsync(cursor, messagesTotal, ct);
            cursor = next;
            if (cursor.Phase == MailboxPhase.Completed)
            {
                await CompleteAsync(cursor, ct);
            }

            if (await ctx.CheckpointAsync(cursor, progress, ct) != JobSignal.Continue)
            {
                return;
            }
        }
    }

    /// <summary>Takes the history ID first, so anything that changes during the pass is replayed from it later.</summary>
    private async Task<(MailboxFetchCursor Cursor, long MessagesTotal)> StartAsync(CancellationToken ct)
    {
        var profile = await gmail.GetProfileAsync(ct);
        var now = time.GetUtcNow();
        await db.FetchState.ExecuteUpdateAsync(set => set
            .SetProperty(f => f.AccountEmail, profile.EmailAddress)
            .SetProperty(f => f.MessagesTotal, profile.MessagesTotal)
            .SetProperty(f => f.MailboxPhase, MailboxPhase.Inbox)
            .SetProperty(f => f.PageToken, (string?)null)
            .SetProperty(f => f.InboxFetched, 0)
            .SetProperty(f => f.AllMailFetched, 0)
            .SetProperty(f => f.StartedAt, now)
            .SetProperty(f => f.CompletedAt, (DateTimeOffset?)null)
            .SetProperty(f => f.UpdatedAt, now), ct);
        return (new MailboxFetchCursor(MailboxPhase.Inbox, null, 0, 0, profile.HistoryId), profile.MessagesTotal);
    }

    private async Task<(MailboxFetchCursor Cursor, JobProgress Progress)> FetchNextChunkAsync(
        MailboxFetchCursor cursor, long? messagesTotal, CancellationToken ct)
    {
        var chunkSize = Math.Clamp(
            (await settings.GetAsync(ct)).FetchChunkSize, SettingsValidation.MinFetchChunkSize, SettingsValidation.MaxFetchChunkSize);
        var inbox = cursor.Phase == MailboxPhase.Inbox;
        var query = new MessageListQuery(null, inbox ? [InboxLabelId] : null, cursor.PageToken, MessageListQuery.MaxPageSize);

        FetchChunkResult chunk;
        try
        {
            chunk = await pipeline.FetchChunkAsync(query, chunkSize, ct);
        }
        catch (GmailInvalidPageTokenException ex) when (cursor.PageToken is not null)
        {
            // The upsert makes re-reading the phase safe; only the phase's counter starts over.
            LogPageTokenRejected(logger, cursor.Phase, ex);
            cursor = inbox ? cursor with { PageToken = null, InboxFetched = 0 } : cursor with { PageToken = null, AllMailFetched = 0 };
            chunk = await pipeline.FetchChunkAsync(query with { PageToken = null }, chunkSize, ct);
        }

        cursor = inbox
            ? cursor with { PageToken = chunk.NextPageToken, InboxFetched = cursor.InboxFetched + chunk.Stored }
            : cursor with { PageToken = chunk.NextPageToken, AllMailFetched = cursor.AllMailFetched + chunk.Stored };
        var progress = inbox
            ? new JobProgress(cursor.InboxFetched, chunk.ResultSizeEstimate, "Fetching Inbox")
            : new JobProgress(cursor.AllMailFetched, messagesTotal, "Fetching All Mail");
        if (chunk.NextPageToken is null)
        {
            cursor = cursor with { Phase = inbox ? MailboxPhase.AllMail : MailboxPhase.Completed };
        }

        await SaveStateAsync(cursor, ct);
        return (cursor, progress);
    }

    private Task SaveStateAsync(MailboxFetchCursor cursor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.FetchState.ExecuteUpdateAsync(set => set
            .SetProperty(f => f.MailboxPhase, cursor.Phase)
            .SetProperty(f => f.PageToken, cursor.PageToken)
            .SetProperty(f => f.InboxFetched, cursor.InboxFetched)
            .SetProperty(f => f.AllMailFetched, cursor.AllMailFetched)
            .SetProperty(f => f.UpdatedAt, now), ct);
    }

    private Task CompleteAsync(MailboxFetchCursor cursor, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.FetchState.ExecuteUpdateAsync(set => set
            .SetProperty(f => f.MailboxPhase, MailboxPhase.Completed)
            .SetProperty(f => f.PageToken, (string?)null)
            .SetProperty(f => f.LastHistoryId, cursor.StartHistoryId)
            .SetProperty(f => f.CompletedAt, now)
            .SetProperty(f => f.UpdatedAt, now), ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail rejected the stored page token; restarting the {Phase} phase from its first page")]
    private static partial void LogPageTokenRejected(ILogger logger, MailboxPhase phase, Exception ex);
}
