using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Additional labels in review (#522): edit validation and canonical storage, DTOs, card outcome and decisions.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewExtraLabelsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Existing = "Synthetic Extra/Known";
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync();
            await db.ActionLog.ExecuteDeleteAsync();
            await db.ActionBatches.ExecuteDeleteAsync();
        }

        await h.InitializeAsync();
        await h.Gmail.Inner.CreateLabelAsync(Existing, Ct);
        h.Services.GetRequiredService<LabelCatalog>().Invalidate();
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Edit_stores_extras_canonical_in_gmails_spelling_and_copies_them_to_the_decision()
    {
        var set = await EditAsync("a01", Edit([" synthetic extra/KNOWN ", "Fresh", "fresh", "Alpha"]));

        set.ExtraLabels.ShouldBe(["Alpha", "Fresh", Existing]);
        set.ExtraLabelsNew.ShouldBe(["Alpha", "Fresh"]);
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a01", Ct)).ExtraLabels.ShouldBe(["Alpha", "Fresh", Existing]);
        (await db.Decisions.AsNoTracking().Where(d => d.MessageId == "a01").OrderByDescending(d => d.CreatedAt).FirstAsync(Ct))
            .ExtraLabels.ShouldBe(["Alpha", "Fresh", Existing]);

        (await EditAsync("a01", Edit(null))).ExtraLabels.ShouldBe(["Alpha", "Fresh", Existing]);
        (await EditAsync("a01", Edit([]))).ExtraLabels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edit_refuses_invalid_extras_naming_each_entry()
    {
        var settings = await SettingsAsync();
        var many = Enumerable.Range(1, ExtraLabelList.Max + 1).Select(i => $"Extra {i}");

        var response = await h.PutAsync(
            $"/api/review/suggestions/{await IdAsync("a01")}",
            Edit([" ", "INBOX", "shopping", settings.ActionLabelName, settings.DeleteLabelName.ToUpperInvariant(), .. many]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors[ExtraLabelList.Field];
        errors.Length.ShouldBe(6);
        errors[0].ShouldStartWith("Entry 1:");
        errors[1].ShouldStartWith("Entry 2 'INBOX':");
        errors[2].ShouldContain("topic label");
        errors[5].ShouldBe($"At most {ExtraLabelList.Max} additional labels.");
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a01", Ct)).ExtraLabels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Edit_refuses_an_extra_equal_to_the_document_type_the_suggestion_keeps()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a01")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DocumentTypeLabel, "Synthetic Type"), Ct);
        }

        var response = await h.PutAsync($"/api/review/suggestions/{await IdAsync("a01")}", Edit(["synthetic type"]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors[ExtraLabelList.Field]
            .ShouldHaveSingleItem().ShouldContain("document-type label");
    }

    [Fact]
    public async Task Group_approve_validates_extras_as_the_edit_does()
    {
        var group = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        var request = (string[] extras) => new GroupDecisionRequest(
            AnalysisRunHarness.Shop, group.GroupKey, group.TopicLabel, group.NeedsAction, group.ToBeDeleted, null, group.DocumentTypeLabel, extras);

        var bad = await h.PostAsync("/api/review/groups/approve", request(["Fine", "INBOX"]));
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors[ExtraLabelList.Field]
            .ShouldHaveSingleItem().ShouldStartWith("Entry 2 'INBOX':");

        // Six entries with one case-insensitive duplicate are five distinct labels: accepted, as the edit accepts them.
        var dup = await h.PostAsync("/api/review/groups/approve", request(["E1", "E2", "E3", "E4", "E5", "e5"]));
        dup.StatusCode.ShouldBe(HttpStatusCode.OK, await dup.Content.ReadAsStringAsync(Ct));
        (await dup.Content.ReadFromJsonAsync<GroupDecisionResponse>(Ct)).ShouldNotBeNull().Changed.ShouldBe(0);
    }

    [Fact]
    public async Task Accepting_a_claude_agree_approves_only_the_members_with_the_cards_extras()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a01").ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraLabels, new[] { "Extra" }), Ct);
        }

        var group = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        group.ExtraLabels.ShouldBeEmpty();
        var created = await h.PostAsync("/api/claude/reviews", new CreateExternalReviewsRequest(
            null, [new GroupRef(AnalysisRunHarness.Shop, group.GroupKey!)], null));
        created.StatusCode.ShouldBe(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        var id = (await created.Content.ReadFromJsonAsync<CreateExternalReviewsResponse>(Ct)).ShouldNotBeNull().Items.ShouldHaveSingleItem().Id;
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var verdict = new ReviewVerdictInput(ReviewVerdict.Agree, null, null, null, null, "Synthetic reasoning", "mcp", null);
            (await scope.ServiceProvider.GetRequiredService<ExternalReviewService>().SubmitVerdictAsync(id, verdict, Ct)).Result
                .ShouldBe(ReviewVerdictResult.Ok);
        }

        var accept = await h.PostAsync($"/api/claude/reviews/{id}/accept", new { });

        accept.StatusCode.ShouldBe(HttpStatusCode.OK, await accept.Content.ReadAsStringAsync(Ct));
        await using var check = postgres.CreateDbContext();
        var statuses = await check.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop)
            .ToDictionaryAsync(s => s.MessageId, s => s.Status, Ct);
        statuses["a01"].ShouldBe(SuggestionStatus.Pending);
        statuses.Where(p => p.Key != "a01").ShouldAllBe(p => p.Value == SuggestionStatus.Approved);
    }

    [Fact]
    public async Task Edit_drops_an_extra_the_new_topic_label_equals()
    {
        await EditAsync("a01", Edit(["Later", "Other"]));

        (await EditAsync("a01", new EditSuggestionRequest("later", false, false))).ExtraLabels.ShouldBe(["Other"]);
    }

    [Fact]
    public async Task Members_with_different_extras_are_different_cards_and_approve_matches_them()
    {
        // Measures the design check: the GROUP BY with the text[] member translates to SQL.
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a01")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraLabels, new[] { "Extra", Existing }), Ct);
        }

        var group = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        (group.Mixed, group.ExtraLabels.Length).ShouldBe((true, 0));
        var member = group.Members.Single(m => m.MessageId == "a01");
        member.ExtraLabels.ShouldBe(["Extra", Existing]);
        member.ExtraLabelsNew.ShouldBe(["Extra"]);

        var approve = await ApproveAsync(group, null);
        (approve.Changed, approve.Skipped.ShouldHaveSingleItem()).ShouldBe((group.Size - 1, await IdAsync("a01")));

        var single = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        single.Mixed.ShouldBeFalse();
        single.ExtraLabels.ShouldBe(["Extra", Existing]);
        single.ExtraLabelsNew.ShouldBe(["Extra"]);
        (await ApproveAsync(single, [])).Changed.ShouldBe(0);
        (await ApproveAsync(single, [Existing.ToLowerInvariant(), "extra"])).Changed.ShouldBe(0);
        (await ApproveAsync(single, ["Extra", Existing])).Changed.ShouldBe(1);
    }

    [Fact]
    public async Task Accepting_an_alternative_keeps_the_extras_but_one_equal_to_its_topic()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a01").ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraLabels, new[] { "Extra", "Synthetic Alternative" }), Ct);
            var row = await db.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a01", Ct);
            var alternative = SuggestionAlternativeRow.From(row, row.Id, row.RunId!.Value, DateTimeOffset.UtcNow);
            alternative.TopicLabel = "Synthetic Alternative";
            db.SuggestionAlternatives.Add(alternative);
            await db.SaveChangesAsync(Ct);
        }

        var response = await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([await IdAsync("a01")], null));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        await using var check = postgres.CreateDbContext();
        var accepted = await check.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a01", Ct);
        accepted.TopicLabel.ShouldBe("Synthetic Alternative");
        accepted.ExtraLabels.ShouldBe(["Extra"]);
    }

    [Fact]
    public async Task An_alternative_shows_its_extras_and_accept_adds_them_to_the_current_ones_up_to_the_cap()
    {
        string[] current = ["Synthetic A", "Synthetic B", "Synthetic C", "Synthetic D"];
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a01" || s.MessageId == "a02")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraLabels, current), Ct);
            foreach (var row in await db.Suggestions.AsNoTracking().Where(s => s.MessageId == "a01" || s.MessageId == "a02").ToListAsync(Ct))
            {
                var alternative = SuggestionAlternativeRow.From(row, row.Id, row.RunId!.Value, DateTimeOffset.UtcNow);
                alternative.ExtraLabels = row.MessageId == "a01" ? [Existing, "Synthetic Z", "synthetic a"] : [];
                db.SuggestionAlternatives.Add(alternative);
            }

            await db.SaveChangesAsync(Ct);
        }

        var members = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.SelectMany(g => g.Members).ToList();
        var shown = members.Single(m => m.MessageId == "a01").Alternative.ShouldNotBeNull();
        shown.ExtraLabels.ShouldBe([Existing, "Synthetic Z", "synthetic a"]);
        shown.ExtraLabelsNew.ShouldBe(["Synthetic Z", "synthetic a"]);
        shown.ExtraLabelsAccepted.ShouldBe(["Synthetic A", "Synthetic B", "Synthetic C", "Synthetic D", Existing]);
        shown.ExtraLabelsNotAdded.ShouldBe(["Synthetic Z"]);
        members.Single(m => m.MessageId == "a02").Alternative.ShouldNotBeNull().ExtraLabels.ShouldBeEmpty();
        (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single(g => g.Members.Any(m => m.MessageId == "a01") && g.Members.Any(m => m.MessageId == "a02"))
            .Alternative.ShouldNotBeNull().Mixed.ShouldBeTrue("a01's and a02's alternatives differ in extras");

        var response = await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([await IdAsync("a01"), await IdAsync("a02")], null));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        await using var check = postgres.CreateDbContext();
        // The cap keeps every current extra; the first added one in canonical order fills the last slot.
        var a01 = await check.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a01", Ct);
        a01.ExtraLabels.ShouldBe(["Synthetic A", "Synthetic B", "Synthetic C", "Synthetic D", Existing]);
        a01.Reason.ShouldEndWith($" {ExtraLabelList.NotAddedNote} Synthetic Z.");
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a02", Ct)).ExtraLabels.ShouldBe(current);
    }

    private static EditSuggestionRequest Edit(string[]? extras) => new("Shopping", false, false, ExtraLabels: extras);

    private async Task<GroupDecisionResponse> ApproveAsync(ReviewGroupDto group, string[]? extras)
    {
        var response = await h.PostAsync("/api/review/groups/approve", new GroupDecisionRequest(
            AnalysisRunHarness.Shop, group.GroupKey, group.TopicLabel, group.NeedsAction, group.ToBeDeleted, null, group.DocumentTypeLabel, extras));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<GroupDecisionResponse>(Ct)).ShouldNotBeNull();
    }

    private async Task<SuggestionDto> EditAsync(string messageId, EditSuggestionRequest request)
    {
        var response = await h.PutAsync($"/api/review/suggestions/{await IdAsync(messageId)}", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SuggestionDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<AppSettings> SettingsAsync()
    {
        await using var scope = h.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(Ct);
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
