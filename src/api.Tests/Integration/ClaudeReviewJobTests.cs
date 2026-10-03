using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The headless Claude review job, its starter and <c>POST /api/claude/test</c> (#166), with <see cref="FakeClaudeCliRunner"/>
/// submitting verdicts over MCP as Claude would. Three items are queued over a finished inbox run: the shop group and the
/// billing suggestions <c>c00</c> and <c>c01</c>, oldest first.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaudeReviewJobTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly PostgresFixture postgres;
    private readonly FakeClaudeCliRunner claude = new();
    private readonly AnalysisRunHarness h;
    private List<Guid> items = [];

    public ClaudeReviewJobTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        h = new(factory, postgres)
        {
            ConfigureServices = s =>
            {
                s.AddSingleton<IClaudeCliRunner>(claude);
                s.PostConfigure<SettingsEnvOptions>(o => o.ClaudeCodeOAuthTokenSet = true);
                // The connection test's MCP self-call reaches the test server in memory.
                s.AddHttpClient(ClaudeTestEndpoint.McpClientName).ConfigurePrimaryHttpMessageHandler(() => h!.Host.Server.CreateHandler());
            },
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync(Ct);
        }

        await h.InitializeAsync();
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync();
        }

        await h.DisposeAsync();
    }

    [Fact]
    public async Task Batches_of_max_items_are_reviewed_over_MCP_and_credited_to_claude_code()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 2);
        claude.Run = async (request, _, ct) =>
        {
            await FakeClaudeCliRunner.SubmitAllAsync(h.Host, request, ct);
            return FakeClaudeCliRunner.Success();
        };
        await QueueItemsAsync();

        var job = await RunJobAsync();

        job.Status.ShouldBe("completed");
        claude.RunCalls.ShouldBe(2);
        var first = claude.Requests.First();
        first.AllowedTools.ShouldBe(ClaudeReviewJob.AllowedTools);
        first.AllowedTools.ShouldAllBe(t => t.StartsWith("mcp__gmail-organiser__"));
        (first.MaxTurns, first.Model, first.Timeout).ShouldBe((AppSettings.DefaultClaudeMaxTurns, null, TimeSpan.FromSeconds(AppSettings.DefaultClaudeRunTimeoutSeconds)));
        first.Prompt.ShouldContain("`limit` 2");
        first.McpEndpointUrl.ShouldEndWith("/mcp");
        first.Token.ShouldBe((await McpTestClient.GetConfigAsync(h.Host, Ct)).Token);
        var rows = await RowsAsync();
        rows.ShouldAllBe(r => r.Status == ExternalReviewStatus.Reviewed && r.Reviewer == ClaudeReviewJob.Reviewer
            && r.ReviewerModel == FakeClaudeCliRunner.FakeModel);
        rows.Select(r => r.BatchId).Distinct().Count().ShouldBe(2);
        ExternalReviewService.Reviewers.ShouldContain(ClaudeReviewJob.Reviewer);
        var last = h.Progress(job.Id).Last();
        (last.Done, last.Total).ShouldBe((3, 3));
    }

    [Fact]
    public async Task Run_without_verdicts_marks_its_batch_unavailable_and_the_next_batch_runs()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 2, model: "synthetic-setting-model");
        claude.Run = async (request, call, ct) =>
        {
            if (call == 2)
            {
                await FakeClaudeCliRunner.SubmitAllAsync(h.Host, request, ct);
            }

            return FakeClaudeCliRunner.Success(model: null);
        };
        await QueueItemsAsync();

        (await RunJobAsync()).Status.ShouldBe("completed");

        claude.Requests.ShouldAllBe(r => r.Model == "synthetic-setting-model");
        var rows = await RowsAsync();
        rows.Take(2).ShouldAllBe(r => r.Status == ExternalReviewStatus.Unavailable && r.Error == ClaudeReviewJob.NoVerdict);
        rows[2].ShouldSatisfyAllConditions(
            r => r.Status.ShouldBe(ExternalReviewStatus.Reviewed),
            r => r.Reviewer.ShouldBe(ClaudeReviewJob.Reviewer),
            r => r.ReviewerModel.ShouldBeNull());
    }

    [Fact]
    public async Task Failed_run_marks_its_batch_unavailable_and_stops_until_a_retry_starts_a_new_job()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 1);
        claude.Run = (_, _, _) => Task.FromResult(ClaudeRunResult.Fail(ClaudeErrorKinds.RateLimit, "Usage limit reached."));
        await QueueItemsAsync();

        var job = await RunJobAsync();

        job.Status.ShouldBe("failed");
        job.Error.ShouldNotBeNull().ShouldContain("rate_limit");
        claude.RunCalls.ShouldBe(1);
        var rows = await RowsAsync();
        rows.Select(r => r.Status).ShouldBe([ExternalReviewStatus.Unavailable, ExternalReviewStatus.Queued, ExternalReviewStatus.Queued]);
        rows[0].Error.ShouldBe("Usage limit reached.");

        (await h.PostWithoutBodyAsync($"/api/claude/reviews/{rows[0].Id}/retry")).EnsureSuccessStatusCode();
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(j => j.Type == ClaudeReviewJob.JobType && j.Status == JobStatus.Queued, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Missing_token_fails_without_starting_the_cli()
    {
        UnsetToken();
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 10);
        await QueueItemsAsync();

        (await RunJobAsync()).Status.ShouldBe("failed");

        claude.RunCalls.ShouldBe(0);
        (await RowsAsync()).ShouldAllBe(r => r.Status == ExternalReviewStatus.Unavailable && r.Error == ClaudeReviewJob.TokenMissingMessage);
    }

    [Fact]
    public async Task Restart_mid_run_closes_the_interrupted_batch_without_running_it_again()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 2);
        using var stop = new CancellationTokenSource();
        claude.Run = async (request, call, ct) =>
        {
            if (call == 1)
            {
                // The host stops while Claude works on the first batch.
                await stop.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }

            await FakeClaudeCliRunner.SubmitAllAsync(h.Host, request, ct);
            return FakeClaudeCliRunner.Success();
        };
        await QueueItemsAsync();

        await h.RunNextAsync(stop.Token);
        (await RowsAsync()).Take(2).ShouldAllBe(r => r.Status == ExternalReviewStatus.Running);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        claude.RunCalls.ShouldBe(2);
        var rows = await RowsAsync();
        rows.Take(2).ShouldAllBe(r => r.Status == ExternalReviewStatus.Unavailable && r.Error == ClaudeReviewJob.Interrupted);
        rows[2].Status.ShouldBe(ExternalReviewStatus.Reviewed);
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.SingleAsync(j => j.Type == ClaudeReviewJob.JobType, Ct)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task Items_decided_before_their_batch_are_cancelled_and_each_run_reviews_only_its_claimed_batch()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 1);
        var submitted = new List<int>();
        claude.Run = async (request, _, ct) =>
        {
            submitted.Add(await FakeClaudeCliRunner.SubmitAllAsync(h.Host, request, ct));
            return FakeClaudeCliRunner.Success();
        };
        await QueueItemsAsync();
        await DecideAsync(s => s.MessageId == "c00");

        (await RunJobAsync()).Status.ShouldBe("completed");

        // c00's batch is never run: the item is cancelled at the claim, not sent to Claude.
        submitted.ShouldBe([1, 1]);
        var rows = await RowsAsync();
        rows.Select(r => r.Status).ShouldBe([ExternalReviewStatus.Reviewed, ExternalReviewStatus.Cancelled, ExternalReviewStatus.Reviewed]);
        rows.Where(r => r.Status == ExternalReviewStatus.Reviewed).ShouldAllBe(r => r.Reviewer == ClaudeReviewJob.Reviewer && r.BatchId != null);
    }

    [Fact]
    public async Task Batch_decided_during_its_run_lists_nothing_so_Claude_never_reviews_the_next_queued_items()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 1);
        var submitted = new List<int>();
        claude.Run = async (request, call, ct) =>
        {
            if (call == 1)
            {
                await DecideAsync(s => s.SenderAddress == AnalysisRunHarness.Shop);
            }

            submitted.Add(await FakeClaudeCliRunner.SubmitAllAsync(h.Host, request, ct));
            return FakeClaudeCliRunner.Success();
        };
        await QueueItemsAsync();

        (await RunJobAsync()).Status.ShouldBe("completed");

        submitted.ShouldBe([0, 1, 1]);
        var rows = await RowsAsync();
        rows[0].Status.ShouldBe(ExternalReviewStatus.Unavailable);
        rows.Skip(1).ShouldAllBe(r => r.Status == ExternalReviewStatus.Reviewed && r.Reviewer == ClaudeReviewJob.Reviewer);
        rows.Select(r => r.BatchId).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task Items_sent_while_the_job_is_finishing_get_a_follow_up_job()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 10);
        claude.Run = async (request, _, ct) =>
        {
            await FakeClaudeCliRunner.SubmitAllAsync(h.Host, request, ct);
            return FakeClaudeCliRunner.Success();
        };
        await QueueItemsAsync();
        await using var db = postgres.CreateDbContext();
        // The job is running but past its last look at the queue when c02 is sent.
        await db.Jobs.Where(j => j.Type == ClaudeReviewJob.JobType).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Running), Ct);
        var late = await QueueAsync(new([await SuggestionIdAsync("c02")], null, null));
        await db.Jobs.Where(j => j.Type == ClaudeReviewJob.JobType && j.DedupKey == ClaudeReviewJob.DedupKey)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Completed), Ct);

        var followUp = await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == ClaudeReviewJob.JobType && j.Status == JobStatus.Queued, Ct);
        followUp.DedupKey.ShouldBe(ClaudeReviewJob.FollowUpDedupKey);
        await h.RunNextAsync();

        claude.RunCalls.ShouldBe(1);
        (await db.ExternalReviews.AsNoTracking().SingleAsync(r => r.Id == late, Ct)).Status.ShouldBe(ExternalReviewStatus.Reviewed);
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == followUp.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task Follow_up_queued_during_a_failed_run_leaves_its_items_queued_until_a_retry_after_the_failure()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 1);
        await QueueItemsAsync();
        var late = Guid.Empty;
        claude.Run = async (_, call, _) =>
        {
            if (call == 1)
            {
                late = await QueueAsync(new([await SuggestionIdAsync("c02")], null, null));
            }

            return ClaudeRunResult.Fail(ClaudeErrorKinds.RateLimit, "Usage limit reached.");
        };

        await h.RunNextAsync();
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == ClaudeReviewJob.DedupKey, Ct)).Status.ShouldBe(JobStatus.Failed);
        var followUp = await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == ClaudeReviewJob.JobType && j.Status == JobStatus.Queued, Ct);
        followUp.DedupKey.ShouldBe(ClaudeReviewJob.FollowUpDedupKey);
        await h.RunNextAsync();

        claude.RunCalls.ShouldBe(1);
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == followUp.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
        var rows = await RowsAsync();
        rows.Select(r => r.Status).ShouldBe([ExternalReviewStatus.Unavailable, ExternalReviewStatus.Queued, ExternalReviewStatus.Queued]);
        (await db.ExternalReviews.AsNoTracking().SingleAsync(r => r.Id == late, Ct)).Status.ShouldBe(ExternalReviewStatus.Queued);

        claude.Run = (_, _, _) => Task.FromResult(FakeClaudeCliRunner.Success());
        (await h.PostWithoutBodyAsync($"/api/claude/reviews/{rows[0].Id}/retry")).EnsureSuccessStatusCode();
        await h.RunNextAsync();

        claude.RunCalls.ShouldBe(5);
        (await db.ExternalReviews.AsNoTracking().CountAsync(r => r.Status == ExternalReviewStatus.Queued, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Send_after_a_failure_replaces_a_job_queued_before_it_so_the_cli_runs()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 10);
        await QueueItemsAsync();
        await using var db = postgres.CreateDbContext();
        var stale = await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == ClaudeReviewJob.JobType, Ct);
        // A follow-up that ran while this job waited behind it, and failed.
        var now = DateTimeOffset.UtcNow;
        db.Jobs.Add(new JobRow
        {
            Id = Guid.CreateVersion7(now),
            Type = ClaudeReviewJob.JobType,
            Queue = JobQueues.Claude,
            DedupKey = ClaudeReviewJob.FollowUpDedupKey,
            Status = JobStatus.Failed,
            Error = "Claude unavailable (rate_limit): Usage limit reached.",
            CreatedAt = stale.CreatedAt.AddMinutes(-1),
            UpdatedAt = now,
            FinishedAt = now,
        });
        await db.SaveChangesAsync(Ct);

        await QueueAsync(new([await SuggestionIdAsync("c02")], null, null));
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == stale.Id, Ct)).Status.ShouldBe(JobStatus.Cancelled);
        await h.RunNextAsync();

        claude.RunCalls.ShouldBe(1);
        (await db.ExternalReviews.AsNoTracking().CountAsync(r => r.Status == ExternalReviewStatus.Queued, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Resuming_a_failed_job_after_a_later_job_failed_runs_the_cli()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 1);
        claude.Run = (_, _, _) => Task.FromResult(ClaudeRunResult.Fail(ClaudeErrorKinds.RateLimit, "Usage limit reached."));
        await QueueItemsAsync();
        var first = await RunJobAsync();
        first.Status.ShouldBe("failed");
        (await h.PostWithoutBodyAsync($"/api/claude/reviews/{items[0]}/retry")).EnsureSuccessStatusCode();
        await h.RunNextAsync();
        claude.RunCalls.ShouldBe(2);
        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(j => j.Type == ClaudeReviewJob.JobType && j.Status == JobStatus.Failed, Ct)).ShouldBe(2);

        claude.Run = (_, _, _) => Task.FromResult(FakeClaudeCliRunner.Success());
        (await h.PostWithoutBodyAsync($"/api/jobs/{first.Id}/resume")).EnsureSuccessStatusCode();
        await h.RunNextAsync();

        claude.RunCalls.ShouldBeGreaterThan(2);
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == first.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
        (await db.ExternalReviews.AsNoTracking().CountAsync(r => r.Status == ExternalReviewStatus.Queued, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Resuming_a_paused_job_after_its_follow_up_failed_runs_the_cli()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 1);
        await QueueItemsAsync();
        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == ClaudeReviewJob.JobType, Ct);
        claude.Run = async (_, call, _) =>
        {
            if (call == 1)
            {
                // A follow-up is queued behind the running job, then the user pauses the running job.
                await QueueAsync(new([await SuggestionIdAsync("c02")], null, null));
                (await h.PostWithoutBodyAsync($"/api/jobs/{job.Id}/pause")).EnsureSuccessStatusCode();
                return FakeClaudeCliRunner.Success();
            }

            return ClaudeRunResult.Fail(ClaudeErrorKinds.RateLimit, "Usage limit reached.");
        };
        await h.RunNextAsync();
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)).Status.ShouldBe(JobStatus.Paused);
        await h.RunNextAsync();
        claude.RunCalls.ShouldBe(2);
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.DedupKey == ClaudeReviewJob.FollowUpDedupKey, Ct)).Status.ShouldBe(JobStatus.Failed);

        claude.Run = (_, _, _) => Task.FromResult(FakeClaudeCliRunner.Success());
        (await h.PostWithoutBodyAsync($"/api/jobs/{job.Id}/resume")).EnsureSuccessStatusCode();
        await h.RunNextAsync();

        claude.RunCalls.ShouldBeGreaterThan(2);
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
    }

    [Theory]
    [InlineData(ClaudeReviewerMode.HeadlessClaudeCode, 1)]
    [InlineData(ClaudeReviewerMode.ClaudeDesktop, 0)]
    [InlineData(ClaudeReviewerMode.Off, 0)]
    public async Task Sending_items_enqueues_one_review_job_only_in_headless_mode(ClaudeReviewerMode mode, int jobs)
    {
        await SetModeAsync(mode, maxItems: 10);

        await QueueItemsAsync();
        await QueueAsync(new([await SuggestionIdAsync("c02")], null, null));

        await using var db = postgres.CreateDbContext();
        var rows = await db.Jobs.Where(j => j.Type == ClaudeReviewJob.JobType).ToListAsync(Ct);
        rows.Count.ShouldBe(jobs);
        rows.ShouldAllBe(j => j.Queue == JobQueues.Claude && j.DedupKey == ClaudeReviewJob.DedupKey && j.Status == JobStatus.Queued);
    }

    [Fact]
    public async Task Connection_test_in_headless_mode_reports_cli_token_and_mcp()
    {
        await SetModeAsync(ClaudeReviewerMode.HeadlessClaudeCode, maxItems: 10);

        var ok = await TestConnectionAsync();
        ok.ShouldBe(ok with { Ok = true, Mode = "headless_claude_code", CliVersion = FakeClaudeCliRunner.FakeVersion, TokenSet = true, McpReachable = true, Error = null });

        UnsetToken();
        claude.Version = new(null, ClaudeErrorKinds.CliMissing, ProcessClaudeCliRunner.CliMissingMessage);
        var failed = await TestConnectionAsync();
        failed.ShouldSatisfyAllConditions(
            r => (r.Ok, r.CliVersion, r.TokenSet, r.McpReachable).ShouldBe((false, null, false, true)),
            r => r.Error.ShouldNotBeNull().ShouldContain("WITH_CLAUDE=true"),
            r => r.Error.ShouldNotBeNull().ShouldContain("CLAUDE_CODE_OAUTH_TOKEN is not set"));
    }

    [Fact]
    public async Task Connection_test_in_desktop_mode_checks_only_mcp_and_off_mode_says_so()
    {
        await SetModeAsync(ClaudeReviewerMode.ClaudeDesktop, maxItems: 10);
        var desktop = await TestConnectionAsync();
        (desktop.Ok, desktop.Mode, desktop.CliVersion, desktop.McpReachable, desktop.Error).ShouldBe((true, "claude_desktop", null, true, null));
        claude.VersionCalls.ShouldBe(0);

        await SetModeAsync(ClaudeReviewerMode.Off, maxItems: 10);
        var off = await TestConnectionAsync();
        (off.Ok, off.Mode, off.McpReachable).ShouldBe((false, "off", false));
        off.Error.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>The user approves the matching suggestions in the portal.</summary>
    private async Task DecideAsync(System.Linq.Expressions.Expression<Func<SuggestionRow, bool>> which)
    {
        await using var db = postgres.CreateDbContext();
        await db.Suggestions.Where(which).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SuggestionStatus.Approved), Ct);
    }

    /// <summary>As if <c>CLAUDE_CODE_OAUTH_TOKEN</c> were empty; the options instance is the host's singleton.</summary>
    private void UnsetToken() =>
        h.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SettingsEnvOptions>>().Value.ClaudeCodeOAuthTokenSet = false;

    private async Task<JobDto> RunJobAsync()
    {
        await h.RunNextAsync();
        await using var scope = h.Services.CreateAsyncScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<IJobService>().ListAsync(activeOnly: false, Ct);
        return jobs.First(j => j.Type == ClaudeReviewJob.JobType);
    }

    private async Task<ClaudeTestResultDto> TestConnectionAsync()
    {
        var response = await h.PostWithoutBodyAsync("/api/claude/test");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClaudeTestResultDto>(Ct)).ShouldNotBeNull();
    }

    private async Task SetModeAsync(ClaudeReviewerMode mode, int maxItems, string? model = null)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
            .UpdateAsync(s => s with { ClaudeReviewerMode = mode, ClaudeMaxItemsPerRun = maxItems, ClaudeModel = model }, Ct);
    }

    /// <summary>Queues the shop group, then <c>c00</c>, then <c>c01</c>, one request each so they are created in that order.</summary>
    private async Task QueueItemsAsync()
    {
        string shopKey;
        await using (var db = postgres.CreateDbContext())
        {
            shopKey = await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop && s.GroupKey != null)
                .Select(s => s.GroupKey!).Distinct().SingleAsync(Ct);
        }

        items =
        [
            await QueueAsync(new(null, [new GroupRef(AnalysisRunHarness.Shop, shopKey)], null)),
            await QueueAsync(new([await SuggestionIdAsync("c00")], null, null)),
            await QueueAsync(new([await SuggestionIdAsync("c01")], null, null)),
        ];
    }

    private async Task<Guid> QueueAsync(CreateExternalReviewsRequest request)
    {
        var response = await h.PostAsync("/api/claude/reviews", request);
        response.EnsureSuccessStatusCode();
        var created = JsonSerializer.Deserialize<CreateExternalReviewsResponse>(await response.Content.ReadAsStringAsync(Ct), JsonSerializerOptions.Web)!;
        return created.Items.Single().Id;
    }

    private async Task<Guid> SuggestionIdAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
    }

    /// <summary>The queued items, in queue order.</summary>
    private async Task<List<ExternalReviewRow>> RowsAsync()
    {
        await using var db = postgres.CreateDbContext();
        var rows = await db.ExternalReviews.AsNoTracking().Where(r => items.Contains(r.Id)).ToListAsync(Ct);
        return [.. items.Select(id => rows.Single(r => r.Id == id))];
    }
}
