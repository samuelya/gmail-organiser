using System.Text.Json.Serialization;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Fetch;

public enum SenderFetchKind
{
    [JsonStringEnumMemberName("address")]
    Address,

    [JsonStringEnumMemberName("domain")]
    Domain,
}

/// <param name="Target">The lower-cased address or domain; <c>SenderQuery</c> matches active jobs on <c>cursor->>'target'</c>.</param>
/// <param name="Total">Gmail's result size estimate from the first page, kept so a resumed job reports the same total.</param>
public sealed record SenderFetchCursor(
    string Target,
    [property: JsonConverter(typeof(JsonStringEnumConverter<SenderFetchKind>))] SenderFetchKind Kind,
    string? PageToken,
    int Fetched,
    long? Total = null)
{
    /// <summary>Gmail search for the target: <c>from:&lt;address&gt;</c> or <c>from:@&lt;domain&gt;</c>.</summary>
    public string Query => Kind == SenderFetchKind.Address ? $"from:{Target}" : $"from:@{Target}";
}

/// <summary>
/// Fetches every message from one sender address or domain via a Gmail <c>from:</c> search (DESIGN §6.1), regardless of
/// how far the mailbox fetch has got. Chunks, upserts and sender stats come from <see cref="MessageFetchPipeline"/>;
/// the page token is checkpointed after every chunk.
/// </summary>
public sealed partial class SenderFetchJob(
    IGmailClient gmail,
    MessageFetchPipeline pipeline,
    LocalAccountClaim accountClaim,
    ISettingsStore settings,
    ILogger<SenderFetchJob> logger) : IJobHandler
{
    public const string JobType = FetchJobTypes.Sender;
    public const string Queue = JobQueues.Fetch;
    private const string ProgressMessage = "Fetching sender";

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<SenderFetchCursor>()
            ?? throw new InvalidOperationException("A sender fetch job needs a cursor with its target.");
        var chunkSize = Math.Clamp(
            (await settings.GetAsync(ct)).FetchChunkSize, SettingsValidation.MinFetchChunkSize, SettingsValidation.MaxFetchChunkSize);

        // On every run, resumes included: the claim is idempotent and must precede the first upsert.
        await accountClaim.ClaimAsync((await gmail.GetProfileAsync(ct)).EmailAddress, ct);

        while (true)
        {
            var query = new MessageListQuery(cursor.Query, null, cursor.PageToken, MessageListQuery.MaxPageSize);
            ListedChunk chunk;
            try
            {
                chunk = await pipeline.ListChunkAsync(query, chunkSize, ct);
            }
            catch (GmailInvalidPageTokenException ex) when (cursor.PageToken is not null)
            {
                // The upsert makes re-reading the listing safe; only the counter starts over.
                LogPageTokenRejected(logger, ex);
                cursor = cursor with { PageToken = null, Fetched = 0 };
                chunk = await pipeline.ListChunkAsync(query with { PageToken = null }, chunkSize, ct);
            }

            var stored = await pipeline.UpsertByIdsAsync(chunk.Ids, ct);
            cursor = cursor with
            {
                PageToken = chunk.NextPageToken,
                Fetched = cursor.Fetched + stored,
                Total = cursor.Total ?? chunk.ResultSizeEstimate,
            };

            if (chunk.NextPageToken is null)
            {
                await ctx.CompleteAsync(cursor, new JobProgress(cursor.Fetched, cursor.Fetched, ProgressMessage), _ => Task.CompletedTask, ct);
                return;
            }

            if (await ctx.CheckpointAsync(cursor, new JobProgress(cursor.Fetched, cursor.Total, ProgressMessage), ct) != JobSignal.Continue)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail rejected the stored page token; restarting the sender fetch from its first page")]
    private static partial void LogPageTokenRejected(ILogger logger, Exception ex);
}
