using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Jobs;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Start-up recovery of runs whose job is gone, the stalled flag, the status filter and resume (#378).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunResumeTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task A_run_whose_job_row_is_gone_is_stalled_until_startup_recovery_fails_it()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.Where(j => j.Id == run.JobId).ExecuteDeleteAsync(Ct);
        }

        var stalled = await h.GetRunAsync(run.Id);
        (stalled.Status, stalled.IsStalled).ShouldBe(("queued", true));

        (await h.Runner.RecoverAsync(Ct)).ShouldBe(0);

        var failed = await h.GetRunAsync(run.Id);
        (failed.Status, failed.IsStalled, failed.Error).ShouldBe(("failed", false, AnalysisRunService.JobEndedError));
        failed.FinishedAt.ShouldNotBeNull();
        (await ListAsync("?status=failed")).ShouldHaveSingleItem().Id.ShouldBe(run.Id);
        (await ListAsync("?status=completed")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Resume_after_the_job_row_is_gone_finishes_the_remaining_groups()
    {
        // The first group is stored, then the model goes away and the job row is lost.
        h.Chat.Respond = (ids, call, _, _) => call == 1
            ? Task.FromResult(AnalysisRunHarness.Agree(ids))
            : throw new InvalidOperationException("Synthetic model outage");
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.ExecuteDeleteAsync(Ct);
        }

        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));
        var job = await ResumeAsync(run.Id);
        var resumed = await h.GetRunAsync(run.Id);
        (resumed.Status, resumed.Error, resumed.JobId, resumed.IsStalled).ShouldBe(("running", (string?)null, job.Id, false));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.FailedMessages).ShouldBe(("completed", 20, 0));
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.CountAsync(s => s.RunId == run.Id, Ct)).ShouldBe(20);
    }

    [Fact]
    public async Task Resume_continues_from_the_cursor_and_retries_failed_messages_once()
    {
        // News output is invalid (6 failed), then billing's call breaks the run.
        h.Chat.Respond = (ids, _, _, _) =>
            ids.Any(id => id.StartsWith('c'))
                ? throw new InvalidOperationException("Synthetic model outage")
                : Task.FromResult(ids.Any(id => id.StartsWith('b')) ? "Sorry, I cannot help with that." : AnalysisRunHarness.Agree(ids));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        var failed = await h.GetRunAsync(run.Id);
        (failed.Status, failed.MessagesCovered, failed.FailedMessages).ShouldBe(("failed", 10, 6));
        failed.Error.ShouldNotBeNull();

        // First resume: news is retried (still invalid), billing breaks again.
        await ResumeAsync(run.Id);
        await h.RunNextAsync();
        var again = await h.GetRunAsync(run.Id);
        (again.Status, again.MessagesCovered, again.FailedMessages).ShouldBe(("failed", 10, 6));

        // Second resume: news was retried already and stays failed; billing now succeeds.
        var before = h.Chat.Requests.Count;
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));
        var job = await ResumeAsync(run.Id);
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.FailedMessages).ShouldBe(("completed", 14, 6));
        h.Chat.Requests.Skip(before).ShouldAllBe(r => r.All(m => !m.Text.Contains("Synthetic body of b")));
        await using var db = postgres.CreateDbContext();
        var cursor = JsonSerializer.Deserialize<AnalysisRunCursor>(
            (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)).Cursor!, JsonSerializerOptions.Web)!;
        (cursor.FailedIds!.Count, cursor.RetriedIds!.Count).ShouldBe((6, 6));
    }

    [Fact]
    public async Task Resume_retried_failures_that_succeed_leave_the_failed_count()
    {
        h.Chat.Respond = (ids, _, _, _) =>
            ids.Any(id => id.StartsWith('c'))
                ? throw new InvalidOperationException("Synthetic model outage")
                : Task.FromResult(ids.Any(id => id.StartsWith('b')) ? "Sorry, I cannot help with that." : AnalysisRunHarness.Agree(ids));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));
        await ResumeAsync(run.Id);
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.FailedMessages).ShouldBe(("completed", 20, 0));
    }

    [Fact]
    public async Task Resume_is_refused_for_active_and_finished_runs()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        (await h.PostWithoutBodyAsync($"/api/analysis/runs/{run.Id}/resume")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await h.RunNextAsync();
        (await h.GetRunAsync(run.Id)).Status.ShouldBe("completed");
        (await h.PostWithoutBodyAsync($"/api/analysis/runs/{run.Id}/resume")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PostWithoutBodyAsync($"/api/analysis/runs/{Guid.NewGuid()}/resume")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await h.GetAsync("/api/analysis/runs?status=paused")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<JobDto> ResumeAsync(Guid runId)
    {
        var response = await h.PostWithoutBodyAsync($"/api/analysis/runs/{runId}/resume");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = (await response.Content.ReadFromJsonAsync<JobDto>(Ct)).ShouldNotBeNull();
        job.Status.ShouldBe("queued");
        return job;
    }

    private async Task<List<AnalysisRunDto>> ListAsync(string query) =>
        (await h.Host.CreateClient().GetFromJsonAsync<List<AnalysisRunDto>>($"/api/analysis/runs{query}", Ct)).ShouldNotBeNull();
}
