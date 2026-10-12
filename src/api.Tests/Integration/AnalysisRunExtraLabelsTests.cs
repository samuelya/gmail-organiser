using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Analysis;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Suggested additional labels end to end (#529): the fake model may only repeat the extras of approved similar decisions
/// in its prompt; representatives, derived members and Review carry what is kept.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunExtraLabelsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string ExtraA = "Synthetic Extra A";
    private const string ExtraB = "Synthetic Extra B";
    private const string Invented = "Synthetic Extra C";
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
            .UpdateAsync(s => s with { AnalysisMemoryShortCircuit = false }, Ct);
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(WithExtras(AnalysisRunHarness.Agree(ids), ExtraA, Invented));
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task A_first_run_without_decisions_stores_no_extras_even_when_the_model_answers_some()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered).ShouldBe(("completed", 20));
        done.MessagesDerived.ShouldBeGreaterThan(0);
        h.Chat.Requests.ShouldAllBe(r => r.All(m => !m.Text.Contains(ExtraLabelList.Field)));
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(Ct)).ShouldAllBe(s => s.ExtraLabels.Length == 0);
    }

    [Fact]
    public async Task After_approving_extras_similar_mail_and_its_derived_members_get_the_allowed_ones_and_unrelated_mail_none()
    {
        await h.StartAsync(new StartAnalysisRunRequest("messages", null, ["a00"], null, null));
        await h.RunNextAsync();
        var first = await IdAsync("a00");
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.AsNoTracking().SingleAsync(s => s.Id == first, Ct)).ExtraLabels.ShouldBeEmpty();
        }

        var edit = await h.PutAsync($"/api/review/suggestions/{first}", new EditSuggestionRequest("Shopping", false, false, ExtraLabels: [ExtraA, ExtraB]));
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(Ct));
        var approve = await h.PostWithoutBodyAsync($"/api/review/suggestions/{first}/approve");
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync(Ct));

        var before = h.Chat.Requests.Count;
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        (await h.GetRunAsync(run.Id)).Status.ShouldBe("completed");
        var requests = h.Chat.Requests.Skip(before).ToList();
        requests.Where(r => r.Any(m => m.Text.Contains("id: a0")))
            .ShouldAllBe(r => r.Any(m => m.Text.Contains($"`{ExtraA}`, `{ExtraB}`")));
        requests.Where(r => !r.Any(m => m.Text.Contains("id: a0")))
            .ShouldAllBe(r => r.All(m => !m.Text.Contains(ExtraLabelList.Field)));
        await using var check = postgres.CreateDbContext();
        var rows = await check.Suggestions.AsNoTracking().Where(s => s.RunId == run.Id).ToListAsync(Ct);
        var shop = rows.Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ToList();
        shop.ShouldContain(s => s.Source == SuggestionSource.Llm);
        shop.ShouldContain(s => s.Source == SuggestionSource.Derived);
        shop.ShouldAllBe(s => s.ExtraLabels.SequenceEqual(new[] { ExtraA }));
        rows.Where(s => s.SenderAddress != AnalysisRunHarness.Shop).ShouldAllBe(s => s.ExtraLabels.Length == 0);

        var member = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.SelectMany(g => g.Members).First(m => m.MessageId != "a00");
        member.ExtraLabels.ShouldBe([ExtraA]);
        member.ExtraLabelsNew.ShouldBe([ExtraA]);
    }

    [Fact]
    public async Task On_a_locked_run_a_new_extra_is_shown_new_and_left_out_of_bulk_approve_but_individual_approve_applies_it()
    {
        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { TaxonomyLocked = true }, Ct);
        }

        await ApproveFirstWithExtraAsync();
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        (await h.GetRunAsync(run.Id)).Status.ShouldBe("completed");
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.AsNoTracking().Where(s => s.RunId == run.Id && s.SenderAddress == AnalysisRunHarness.Shop).ToListAsync(Ct))
                .ShouldAllBe(s => s.ExtraLabels.SequenceEqual(new[] { ExtraA }));
        }

        var member = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.SelectMany(g => g.Members).First(m => m.MessageId != "a00");
        member.ExtraLabelsNew.ShouldBe([ExtraA]);
        member.NewLabelPending.ShouldBeTrue();
        var bulk = await h.PostAsync("/api/review/bulk-approve", new BulkApproveRequest(0.5, IncludeDerived: true, SenderAddress: AnalysisRunHarness.Shop));
        var result = (await bulk.Content.ReadFromJsonAsync<BulkApproveResponse>(Ct)).ShouldNotBeNull();
        result.Approved.ShouldBe(0);
        result.ExcludedNewLabel.ShouldBeGreaterThan(1);

        var approve = await h.PostWithoutBodyAsync($"/api/review/suggestions/{member.Id}/approve");
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync(Ct));
        (await h.PostAsync("/api/review/apply", new ApplyRequest(SuggestionIds: [member.Id]))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        var created = (await h.Gmail.ListLabelsAsync(Ct)).Single(l => l.Name == ExtraA);
        h.Gmail.Inner.Messages.Single(m => m.Id == member.MessageId).LabelIds.ShouldContain(created.Id);
    }

    private async Task ApproveFirstWithExtraAsync()
    {
        await h.StartAsync(new StartAnalysisRunRequest("messages", null, ["a00"], null, null));
        await h.RunNextAsync();
        var first = await IdAsync("a00");
        var edit = await h.PutAsync($"/api/review/suggestions/{first}", new EditSuggestionRequest("Shopping", false, false, ExtraLabels: [ExtraA]));
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(Ct));
        var approve = await h.PostWithoutBodyAsync($"/api/review/suggestions/{first}/approve");
        approve.StatusCode.ShouldBe(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync(Ct));
    }

    private static string WithExtras(string answer, params string[] extras)
    {
        var node = JsonNode.Parse(answer)!;
        foreach (var suggestion in node["suggestions"]!.AsArray())
        {
            suggestion!["extraLabels"] = new JsonArray([.. extras.Select(e => JsonValue.Create(e))]);
        }

        return node.ToJsonString();
    }

    private async Task<Guid> IdAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
    }

    private async Task<ReviewSenderDetailDto> DetailAsync(string address)
    {
        var response = await h.GetAsync($"/api/review/senders/{Uri.EscapeDataString(address)}?status=pending");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull();
    }
}
