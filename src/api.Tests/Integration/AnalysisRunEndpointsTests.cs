using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Theory]
    [InlineData("""{"scope":"everything"}""", "scope")]
    [InlineData("""{"scope":"inbox","count":0}""", "count")]
    [InlineData("""{"scope":"inbox","count":1001}""", "count")]
    [InlineData("""{"scope":"sender"}""", "senderAddress")]
    [InlineData("""{"scope":"sender","senderAddress":"nobody@example.com"}""", "senderAddress")]
    [InlineData("""{"scope":"messages","messageIds":[]}""", "messageIds")]
    [InlineData("""{"scope":"inbox","groupingMode":"clusters"}""", "groupingMode")]
    public async Task Start_rejects_an_invalid_request(string json, string field)
    {
        var response = await h.PostAsync("/api/analysis/runs", System.Text.Json.JsonDocument.Parse(json).RootElement);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain($"\"{field}\"");
        await using var db = postgres.CreateDbContext();
        (await db.AnalysisRuns.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Start_is_409_without_a_chat_model()
    {
        await h.SetChatModelAsync(null);

        var response = await h.PostAsync("/api/analysis/runs", new StartAnalysisRunRequest("inbox", null, null, 5, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var db = postgres.CreateDbContext();
        (await db.AnalysisRuns.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Runs_queue_one_job_each_and_list_newest_first()
    {
        var first = await h.StartAsync(new StartAnalysisRunRequest("sender", " SHOP@example.com ", null, null, "off"));
        var second = await h.StartAsync(new StartAnalysisRunRequest("messages", null, ["x00", "x01", "x00"], 999, null));

        (first.SenderAddress, first.GroupingMode, first.RequestedCount).ShouldBe((AnalysisRunHarness.Shop, "off", 20));
        (second.Scope, second.RequestedCount, second.GroupingMode).ShouldBe(("messages", 2, "auto"));
        second.JobId.ShouldNotBe(first.JobId);
        var active = await h.GetAsync("/api/analysis/runs?active=true");
        (await active.Content.ReadFromJsonAsync<List<AnalysisRunDto>>(Ct))!.Select(r => r.Id).ShouldBe([second.Id, first.Id]);
        (await h.GetAsync($"/api/analysis/runs/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await h.GetAsync("/api/analysis/runs?limit=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // One run at a time on the analysis queue: the sender run (all ten shop mails, one by one), then the messages run.
        await h.RunNextAsync();
        await h.RunNextAsync();
        var finished = await (await h.GetAsync("/api/analysis/runs?active=false&limit=1")).Content.ReadFromJsonAsync<List<AnalysisRunDto>>(Ct);
        finished!.ShouldHaveSingleItem().Id.ShouldBe(second.Id);
        (await h.GetRunAsync(first.Id)).ShouldSatisfyAllConditions(
            r => r.Status.ShouldBe("completed"), r => r.MessagesCovered.ShouldBe(10), r => r.LlmCalls.ShouldBe(10));
        (await h.GetRunAsync(second.Id)).MessagesCovered.ShouldBe(2);
    }

    [Fact]
    public async Task Cancelling_a_queued_run_ends_it_at_once_and_a_finished_run_is_409()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 5, null));

        var cancelled = await h.PostAsync($"/api/analysis/runs/{run.Id}/cancel", new { });

        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cancelled.Content.ReadFromJsonAsync<AnalysisRunDto>(Ct))!.Status.ShouldBe("cancelled");
        (await h.Runner.ClaimAsync(Ct)).ShouldBeEmpty();
        (await h.PostAsync($"/api/analysis/runs/{run.Id}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PostAsync($"/api/analysis/runs/{Guid.NewGuid()}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reanalyse_resets_pending_and_rejected_suggestions_and_keeps_decided_ones()
    {
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Approved);
            await AnalysisRunHarness.DecideAsync(db, "a01", SuggestionStatus.Rejected);
        }

        var bySender = await h.PostAsync("/api/analysis/re-analyse", new ReanalyseRequest(null, AnalysisRunHarness.Shop));

        bySender.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await bySender.Content.ReadFromJsonAsync<ReanalyseResponse>(Ct))!.Reset.ShouldBe(9);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop).Select(s => s.MessageId).ToListAsync(Ct))
                .ShouldBe(["a00"]);
            (await db.Messages.Where(m => m.FromAddress == AnalysisRunHarness.Shop && m.AnalysisStatus == AnalysisStatus.NotAnalysed)
                .CountAsync(Ct)).ShouldBe(9);
            (await db.Senders.SingleAsync(s => s.Address == AnalysisRunHarness.Shop, Ct)).AnalysedCount.ShouldBe(1);
        }

        (await h.PostAsync("/api/analysis/re-analyse", new ReanalyseRequest(["a00"], null))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var byId = await h.PostAsync("/api/analysis/re-analyse", new ReanalyseRequest(["b00", "x00"], null));
        (await byId.Content.ReadFromJsonAsync<ReanalyseResponse>(Ct))!.Reset.ShouldBe(1);
        (await h.PostAsync("/api/analysis/re-analyse", new ReanalyseRequest(["b01"], AnalysisRunHarness.News)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/analysis/re-analyse", new ReanalyseRequest(["bad id"], null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Summary_counts_statuses_applied_flags_and_savings()
    {
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Approved);
            await AnalysisRunHarness.DecideAsync(db, "b00", SuggestionStatus.Applied, s => s.ToBeDeleted = true);
            await AnalysisRunHarness.DecideAsync(db, "c00", SuggestionStatus.Applied, s => s.NeedsAction = true);
            await AnalysisRunHarness.DecideAsync(db, "c01", SuggestionStatus.Rejected);
        }

        var summary = await (await h.GetAsync("/api/analysis/summary")).Content.ReadFromJsonAsync<AnalysisSummaryDto>(Ct);

        summary.ShouldBe(new AnalysisSummaryDto(5, 16, 1, 1, 2, 1, 1, 3, 20, 1 - (3 / 20.0)));
    }
}
