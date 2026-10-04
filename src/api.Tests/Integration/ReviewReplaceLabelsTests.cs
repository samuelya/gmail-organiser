using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Labelled phase (#200): the billing messages carry a synthetic personal label the model replaces; the model's
/// <c>replaceLabels</c> are stored (derived members included), shown in the review DTOs and editable.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewReplaceLabelsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string OldLabel = "Old/Invoices";
    private const string Topic = "Finance/Invoices";
    private readonly AnalysisRunHarness h = new(factory, postgres);
    private string oldId = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        oldId = (await h.Gmail.Inner.CreateLabelAsync(OldLabel, Ct)).Id;
        string[] labels = ["INBOX", "CATEGORY_UPDATES", oldId];
        string[] billing = ["c00", "c01", "c02", "c03"];
        foreach (var id in billing)
        {
            h.Gmail.Inner.SetLabels(id, labels);
        }

        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => billing.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, labels), Ct);
        }

        h.Services.GetRequiredService<LabelCatalog>().Invalidate();
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(JsonSerializer.Serialize(new
        {
            suggestions = ids.Select(id => new
            {
                id,
                topicLabel = id[0] == 'c' ? Topic : AnalysisRunHarness.LabelFor(id),
                isNewLabel = false,
                needsAction = false,
                toBeDeleted = false,
                unsubscribeSuggested = false,
                confidence = 0.9,
                reason = "Synthetic reason",
                replaceLabels = id[0] == 'c' ? new[] { OldLabel } : [],
            }),
        }));
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Model_and_derived_suggestions_store_the_replaced_labels_and_the_review_shows_a_move()
    {
        await using (var db = postgres.CreateDbContext())
        {
            var rows = await db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Billing).ToListAsync(Ct);
            rows.Count.ShouldBe(4);
            rows.ShouldContain(s => s.Source == SuggestionSource.Derived);
            rows.ShouldAllBe(s => s.ReplaceLabels.SequenceEqual(new[] { OldLabel }) && s.ReplaceLabelIds.SequenceEqual(new[] { oldId }));
            (await db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ToListAsync(Ct))
                .ShouldAllBe(s => s.ReplaceLabels.Length == 0);
        }

        var group = (await DetailAsync(AnalysisRunHarness.Billing)).Groups.Single();
        (group.LabelChange, group.ReplaceLabels.ShouldHaveSingleItem()).ShouldBe((LabelChange.Move, OldLabel));
        group.Members.ShouldAllBe(m => m.LabelChange == LabelChange.Move && m.CurrentLabels.Single() == OldLabel);
        var shop = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        (shop.LabelChange, shop.ReplaceLabels.Count).ShouldBe((LabelChange.None, 0));

        var raw = await (await h.GetAsync($"/api/review/senders/{Uri.EscapeDataString(AnalysisRunHarness.Billing)}")).Content.ReadAsStringAsync(Ct);
        raw.ShouldContain("\"labelChange\":\"move\"");
    }

    [Fact]
    public async Task Edit_accepts_only_the_message_s_current_labels_in_gmail_s_spelling()
    {
        var id = await IdAsync("c00");

        var missing = await h.PutAsync($"/api/review/suggestions/{Guid.NewGuid()}", new EditSuggestionRequest(Topic, false, false, ["Synthetic/Nope"]));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var unknown = await h.PutAsync($"/api/review/suggestions/{id}", new EditSuggestionRequest(Topic, false, false, ["Old/Invoices", "Synthetic/Nope"]));
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync(Ct)).ShouldContain("replaceLabels");

        var cleared = await EditAsync(id, new EditSuggestionRequest(Topic, false, false, []));
        (cleared.ReplaceLabels.Count, cleared.LabelChange, cleared.Edited).ShouldBe((0, LabelChange.Add, true));

        var set = await EditAsync(id, new EditSuggestionRequest("Finance/Other", false, false, ["old/invoices"]));
        (set.ReplaceLabels.ShouldHaveSingleItem(), set.LabelChange).ShouldBe((OldLabel, LabelChange.Relabel));

        var kept = await EditAsync(id, new EditSuggestionRequest("Finance/Other", true, false));
        kept.ReplaceLabels.ShouldBe([OldLabel]);

        // The topic becomes the replaced label: apply keeps it, and the card says so.
        var same = await EditAsync(id, new EditSuggestionRequest(OldLabel, true, false));
        (same.ReplaceLabels.Count, same.LabelChange).ShouldBe((0, LabelChange.Keep));
    }

    [Fact]
    public async Task A_replaced_label_renamed_in_gmail_shows_its_new_name()
    {
        h.Gmail.Inner.RenameLabel(oldId, "Archive/Invoices");
        h.Services.GetRequiredService<LabelCatalog>().Invalidate();

        var member = (await DetailAsync(AnalysisRunHarness.Billing)).Groups.Single().Members[0];

        (member.ReplaceLabels.ShouldHaveSingleItem(), member.CurrentLabels.ShouldHaveSingleItem(), member.LabelChange)
            .ShouldBe(("Archive/Invoices", "Archive/Invoices", LabelChange.Move));
    }

    [Fact]
    public async Task Without_the_label_list_an_edit_of_the_replaced_labels_is_refused_and_nothing_changes()
    {
        var id = await IdAsync("c00");
        var key = await KeyOfAsync("c00");
        h.Gmail.AfterListLabels = () => throw new HttpRequestException("Synthetic outage");
        h.Services.GetRequiredService<LabelCatalog>().Invalidate();

        var missing = await h.PutAsync($"/api/review/suggestions/{Guid.NewGuid()}", new EditSuggestionRequest(Topic, false, false, [OldLabel]));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var edit = await h.PutAsync($"/api/review/suggestions/{id}", new EditSuggestionRequest(Topic, false, false, [OldLabel]));
        edit.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var group = await h.PostAsync(
            "/api/review/groups/approve", new GroupDecisionRequest(AnalysisRunHarness.Billing, key, Topic, false, false, [OldLabel]));
        group.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Billing).ToListAsync(Ct))
            .ShouldAllBe(s => s.ReplaceLabelIds.Length == 1 && !s.Edited && s.Status == SuggestionStatus.Pending);
    }

    [Fact]
    public async Task Group_approve_with_the_card_s_labels_keeps_each_member_s_own_and_adds_none()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "c00").ExecuteUpdateAsync(
                s => s.SetProperty(x => x.ReplaceLabelIds, Array.Empty<string>()).SetProperty(x => x.ReplaceLabels, Array.Empty<string>()), Ct);
        }

        var group = (await DetailAsync(AnalysisRunHarness.Billing)).Groups.Single();
        group.ReplaceLabels.ShouldBe([OldLabel]);
        var response = await h.PostAsync(
            "/api/review/groups/approve",
            new GroupDecisionRequest(AnalysisRunHarness.Billing, group.GroupKey, group.TopicLabel, false, false, [OldLabel.ToUpperInvariant()]));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        await using var after = postgres.CreateDbContext();
        var rows = await after.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Billing).ToListAsync(Ct);
        rows.ShouldAllBe(s => !s.Edited && s.Status == SuggestionStatus.Approved);
        rows.Single(s => s.MessageId == "c00").ReplaceLabelIds.ShouldBeEmpty();
        rows.Where(s => s.MessageId != "c00").ShouldAllBe(s => s.ReplaceLabelIds.SequenceEqual(new[] { oldId }));
    }

    [Fact]
    public async Task Group_approve_can_change_the_replaced_labels_of_the_members_it_approves()
    {
        var group = (await DetailAsync(AnalysisRunHarness.Billing)).Groups.Single();
        var unknown = await h.PostAsync(
            "/api/review/groups/approve",
            new GroupDecisionRequest(AnalysisRunHarness.Billing, group.GroupKey, group.TopicLabel, false, false, ["Synthetic/Nope"]));
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync(Ct)).ShouldContain("replaceLabels");

        var response = await h.PostAsync(
            "/api/review/groups/approve", new GroupDecisionRequest(AnalysisRunHarness.Billing, group.GroupKey, group.TopicLabel, false, false, []));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<GroupDecisionResponse>(Ct)).ShouldNotBeNull().Changed.ShouldBe(4);

        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Billing).ToListAsync(Ct);
        rows.ShouldAllBe(s => s.ReplaceLabels.Length == 0 && s.Edited && s.Status == SuggestionStatus.Approved);
    }

    private async Task<SuggestionDto> EditAsync(Guid id, EditSuggestionRequest request)
    {
        var response = await h.PutAsync($"/api/review/suggestions/{id}", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SuggestionDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<string?> KeyOfAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.GroupKey).SingleAsync(Ct);
    }

    private async Task<Guid> IdAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
    }

    private async Task<ReviewSenderDetailDto> DetailAsync(string address, string status = "pending")
    {
        var response = await h.GetAsync($"/api/review/senders/{Uri.EscapeDataString(address)}?status={status}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull();
    }
}
