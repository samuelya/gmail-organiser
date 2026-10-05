using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Analysis;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Mail type and proposed new labels (#365) over a run of the harness mailbox: shop 10 (marketing), news 6 (newsletter),
/// billing 4 (action_bill).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SuggestionMailTypeTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Proposed = "Synthetic/Proposed";

    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Run_stores_the_mail_type_derived_members_inherit_it_and_a_proposed_label_becomes_the_topic()
    {
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(WithProposedForBilling(AnalysisRunHarness.Agree(ids)));

        await RunAsync();

        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().ToListAsync(Ct);
        rows.Count.ShouldBe(20);
        rows.ShouldContain(s => s.Source == SuggestionSource.Derived);
        rows.Where(s => s.MessageId.StartsWith('a')).ShouldAllBe(s => s.MailType == MailType.Marketing && s.ProposedNewLabel == null);
        rows.Where(s => s.MessageId.StartsWith('b')).ShouldAllBe(s => s.MailType == MailType.Newsletter);
        var billing = rows.Where(s => s.MessageId.StartsWith('c')).ToList();
        billing.ShouldAllBe(s => s.MailType == MailType.ActionBill && s.TopicLabel == Proposed && s.IsNewLabel);
        billing.Where(s => s.Source == SuggestionSource.Llm).ShouldAllBe(s => s.ProposedNewLabel == Proposed);
    }

    [Fact]
    public async Task Representatives_that_disagree_on_the_mail_type_are_analysed_one_by_one()
    {
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(ids.Count > 1 && ids[0].StartsWith('a')
            ? SetMailType(AnalysisRunHarness.Agree(ids), (id, i) => i == 0 ? "receipt" : "marketing")
            : AnalysisRunHarness.Agree(ids));

        await RunAsync();

        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.CountAsync(s => s.MessageId.StartsWith("a") && s.Source == SuggestionSource.Derived, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Custom_template_without_mail_types_stores_none()
    {
        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
                .UpdateAsync(s => s with { AnalysisPromptTemplate = "Classify.\n{{labelTree}}\n{{emails}}" }, Ct);
        }

        await RunAsync();

        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.CountAsync(Ct)).ShouldBe(20);
        (await db.Suggestions.CountAsync(s => s.MailType != null, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Review_dto_shows_the_mail_type_edit_changes_or_clears_it_and_the_decision_stores_it()
    {
        await RunAsync();
        var id = await IdAsync("c00");

        (await EditAsync(id, "receipt")).MailType.ShouldBe("receipt");
        (await EditAsync(id, null)).MailType.ShouldBe("receipt");
        (await EditAsync(id, "")).MailType.ShouldBeNull();
        var invalid = await h.PutAsync($"/api/review/suggestions/{id}", new EditSuggestionRequest("Finance", false, false, MailType: "spam"));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync(Ct)).ShouldContain("mailType");

        var other = await IdAsync("c01");
        var approved = await h.PostAsync($"/api/review/suggestions/{other}/approve", new { });
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await approved.Content.ReadFromJsonAsync<SuggestionDto>(Ct)).ShouldNotBeNull().MailType.ShouldBe("action_bill");

        await using var db = postgres.CreateDbContext();
        (await db.Decisions.AsNoTracking().SingleAsync(d => d.MessageId == "c01", Ct)).MailType.ShouldBe(MailType.ActionBill);
    }

    private async Task RunAsync()
    {
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
    }

    private async Task<SuggestionDto> EditAsync(Guid id, string? mailType)
    {
        var response = await h.PutAsync($"/api/review/suggestions/{id}", new EditSuggestionRequest("Finance", true, false, MailType: mailType));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SuggestionDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<Guid> IdAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
    }

    private static string WithProposedForBilling(string answer) =>
        Rewrite(answer, (item, _) => item["proposedNewLabel"] = item["id"]!.GetValue<string>().StartsWith('c') ? Proposed : null);

    private static string SetMailType(string answer, Func<string, int, string> mailType) =>
        Rewrite(answer, (item, i) => item["mailType"] = mailType(item["id"]!.GetValue<string>(), i));

    private static string Rewrite(string answer, Action<JsonObject, int> change)
    {
        var root = JsonNode.Parse(answer)!.AsObject();
        var items = root["suggestions"]!.AsArray();
        for (var i = 0; i < items.Count; i++)
        {
            change(items[i]!.AsObject(), i);
        }

        return root.ToJsonString(JsonSerializerOptions.Web);
    }
}
