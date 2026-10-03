using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Mcp;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Claude;

/// <param name="BatchId">The batch taken but not yet closed; set before its items are taken.</param>
/// <param name="Done">Items closed so far (reviewed or unavailable).</param>
public sealed record ClaudeReviewCursor(Guid? BatchId, long Done);

/// <summary>
/// Headless Claude review (DESIGN §6.7), one job at a time (dedup key <see cref="DedupKey"/> on queue
/// <see cref="JobQueues.Claude"/>): takes at most <see cref="AppSettings.ClaudeMaxItemsPerRun"/> queued items, marks them
/// running as one batch and runs <c>claude -p</c> over them; Claude submits verdicts through <c>/mcp</c>. Afterwards the
/// batch's reviewed items are credited to <see cref="Reviewer"/> and the rest become unavailable. A failed run (no CLI, no
/// token, auth, rate limit, timeout) marks its batch unavailable and fails the job, leaving the queue until the user
/// sends or retries; it never retries by itself, and a job enqueued before that failure completes without running (see
/// <see cref="EnqueuedBeforeFailureAsync"/>). A batch interrupted by a restart is closed as unavailable, never run
/// twice. A pause or cancel takes effect after the current batch.
/// </summary>
public sealed class ClaudeReviewJob(
    AppDbContext db,
    ExternalReviewService reviews,
    IClaudeCliRunner runner,
    ISettingsStore settings,
    IOptions<SettingsEnvOptions> env,
    McpTokenService mcpTokens,
    IServer server,
    ILogger<ClaudeReviewJob> logger) : IJobHandler
{
    public const string JobType = ClaudeJobTypes.Review;
    public const string DedupKey = "single";

    /// <summary>The job <see cref="HeadlessClaudeReviewStarter"/> adds behind a running one, for items it may miss.</summary>
    public const string FollowUpDedupKey = "follow-up";
    public const string Reviewer = "claude_code";
    public const string NoVerdict = "No verdict submitted.";
    public const string Interrupted = "The Claude run was interrupted by an api restart; retry to send the item again.";
    public const string TokenMissingMessage =
        "CLAUDE_CODE_OAUTH_TOKEN is not set. Run `claude setup-token`, put the token in .env and restart the api.";

    /// <summary>The review tools, as Claude Code names MCP tools; nothing else (no Bash, Read, Write or web tools).</summary>
    public static readonly IReadOnlyList<string> AllowedTools =
        [.. new[] { "list_pending_reviews", "get_review_item", "get_label_tree", "submit_review" }
            .Select(t => $"mcp__{McpExtensions.ServerName}__{t}")];

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<ClaudeReviewCursor>() ?? new(null, 0);
        if (cursor.BatchId is { } interrupted)
        {
            var closed = await CloseBatchAsync(interrupted, ClaudeRunResult.Fail(ClaudeErrorKinds.Failed, Interrupted), ct);
            cursor = new(null, cursor.Done + closed);
            if (await ctx.CheckpointAsync(cursor, await ProgressAsync(cursor, ct), ct) != JobSignal.Continue)
            {
                return;
            }
        }

        if (await EnqueuedBeforeFailureAsync(db, ctx.JobId, ct))
        {
            // The queue stops after a failed run until the user sends or retries; the items stay queued for the job
            // that send or retry starts.
            await ctx.CompleteAsync(cursor, await ProgressAsync(cursor, ct), _ => Task.CompletedTask, ct);
            return;
        }

        while (true)
        {
            var s = await settings.GetAsync(ct);
            var ids = s.ClaudeReviewerMode == ClaudeReviewerMode.HeadlessClaudeCode
                ? await db.ExternalReviews.AsNoTracking().Where(r => r.Status == ExternalReviewStatus.Queued)
                    .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(s.ClaudeMaxItemsPerRun).Select(r => r.Id).ToListAsync(ct)
                : [];
            if (ids.Count == 0)
            {
                // Done, or the mode changed: anything still queued waits for the next send. Items sent from here on
                // get a follow-up job from the starter, since this one is still running.
                await ctx.CompleteAsync(cursor, await ProgressAsync(cursor, ct), _ => Task.CompletedTask, ct);
                return;
            }

            var batchId = Guid.NewGuid();
            if (await ctx.CheckpointAsync(cursor with { BatchId = batchId }, await ProgressAsync(cursor, ct), ct) != JobSignal.Continue)
            {
                return;
            }

            var taken = await reviews.MarkRunningAsync(ids, batchId, ct);
            var result = taken == 0 ? null : await RunBatchAsync(s, taken, ct);
            var done = result is null ? 0 : await CloseBatchAsync(batchId, result, ct);
            cursor = new(null, cursor.Done + done);
            var signal = await ctx.CheckpointAsync(cursor, await ProgressAsync(cursor, ct), ct);
            if (result is { Ok: false })
            {
                throw new JobRefusedException($"Claude unavailable ({result.ErrorKind}): {result.Error}");
            }

            if (signal != JobSignal.Continue)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Whether another review job failed after <paramref name="jobId"/> was enqueued: the job then belongs to a send or
    /// retry made before the failure (a follow-up, or one queued behind a running job) and must not start the CLI.
    /// </summary>
    public static async Task<bool> EnqueuedBeforeFailureAsync(AppDbContext db, Guid jobId, CancellationToken ct)
    {
        var createdAt = await db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.CreatedAt).SingleAsync(ct);
        return await db.Jobs.AnyAsync(
            j => j.Type == JobType && j.Id != jobId && j.Status == JobStatus.Failed && j.FinishedAt > createdAt, ct);
    }

    private async Task<ClaudeRunResult> RunBatchAsync(AppSettings s, int count, CancellationToken ct)
    {
        if (!env.Value.ClaudeCodeOAuthTokenSet)
        {
            return ClaudeRunResult.Fail(ClaudeErrorKinds.TokenMissing, TokenMissingMessage);
        }

        var request = new ClaudeRunRequest(
            ReviewPrompts.Render(count),
            ClaudeExtensions.McpSelfUrl(server),
            await mcpTokens.GetOrCreateAsync(ct),
            AllowedTools,
            s.ClaudeMaxTurns,
            s.ClaudeModel,
            TimeSpan.FromSeconds(s.ClaudeRunTimeoutSeconds));
        try
        {
            return await runner.RunAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("The Claude runner threw {Error}", ex.GetType().Name);
            return ClaudeRunResult.Fail(ClaudeErrorKinds.Failed, "The Claude run failed unexpectedly; see the api log.");
        }
    }

    /// <summary>Credits the reviewed items and closes the still-running ones; returns how many items the batch held.</summary>
    private async Task<int> CloseBatchAsync(Guid batchId, ClaudeRunResult result, CancellationToken ct)
    {
        var reviewed = await reviews.SetBatchReviewerAsync(batchId, Reviewer, result.Model, ct);
        var running = await db.ExternalReviews.AsNoTracking()
            .Where(r => r.BatchId == batchId && r.Status == ExternalReviewStatus.Running).Select(r => r.Id).ToListAsync(ct);
        var unavailable = running.Count == 0 ? 0 : await reviews.MarkUnavailableAsync(running, result.Ok ? NoVerdict : result.Error ?? NoVerdict, ct);
        logger.LogInformation(
            "Claude review batch {BatchId}: {Reviewed} reviewed, {Unavailable} unavailable, {Kind}, {Turns} turns, {ElapsedMs} ms",
            batchId, reviewed, unavailable, result.ErrorKind ?? "ok", result.NumTurns, (long)result.Elapsed.TotalMilliseconds);
        return await db.ExternalReviews.CountAsync(r => r.BatchId == batchId, ct);
    }

    /// <summary>Items done of the items done plus those still queued.</summary>
    private async Task<JobProgress> ProgressAsync(ClaudeReviewCursor cursor, CancellationToken ct)
    {
        var queued = await db.ExternalReviews.CountAsync(r => r.Status == ExternalReviewStatus.Queued, ct);
        return new JobProgress(cursor.Done, cursor.Done + queued, null);
    }
}
