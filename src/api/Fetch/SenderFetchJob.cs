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
public sealed record SenderFetchCursor(
    string Target,
    [property: JsonConverter(typeof(JsonStringEnumConverter<SenderFetchKind>))] SenderFetchKind Kind,
    string? PageToken,
    int Fetched)
{
    /// <summary>Gmail search for the target: <c>from:&lt;address&gt;</c> or <c>from:@&lt;domain&gt;</c>.</summary>
    [JsonIgnore]
    public string Query => Kind == SenderFetchKind.Address ? $"from:{Target}" : $"from:@{Target}";
}

/// <summary>
/// Fetches every message from one sender address or domain via a Gmail <c>from:</c> search (DESIGN §6.1), regardless of
/// how far the mailbox fetch has got. Chunks, upserts and sender stats come from <see cref="MessageFetchPipeline"/>;
/// the page token is checkpointed after every chunk. Gmail has no exact count for a search, and its per-page
/// <c>resultSizeEstimate</c> is a guess, so a running job reports an unknown total; completion reports total = fetched.
/// A cursor saved by an older version with a <c>total</c> field still resumes: the unknown property is ignored.
/// </summary>
public sealed class SenderFetchJob(
    IGmailClient gmail,
    MessageFetchPipeline pipeline,
    LocalAccountClaim accountClaim,
    ISettingsStore settings) : IJobHandler
{
    public const string JobType = FetchJobTypes.Sender;
    public const string Queue = JobQueues.Fetch;
    private const string ProgressMessage = "Fetching sender";
    private const string CompletedMessage = "Sender fetch complete";

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
            var chunk = await pipeline.ListChunkAsync(query, chunkSize, ct);
            if (chunk.Restarted)
            {
                // The pipeline listed from the first page again; the counter starts over with it.
                cursor = cursor with { Fetched = 0 };
            }

            var stored = await pipeline.UpsertByIdsAsync(chunk.Ids, ct);
            cursor = cursor with
            {
                PageToken = chunk.NextPageToken,
                Fetched = cursor.Fetched + stored,
            };

            if (chunk.NextPageToken is null)
            {
                await ctx.CompleteAsync(cursor, new JobProgress(cursor.Fetched, cursor.Fetched, CompletedMessage), _ => Task.CompletedTask, ct);
                return;
            }

            if (await ctx.CheckpointAsync(cursor, new JobProgress(cursor.Fetched, null, ProgressMessage), ct) != JobSignal.Continue)
            {
                return;
            }
        }
    }
}
