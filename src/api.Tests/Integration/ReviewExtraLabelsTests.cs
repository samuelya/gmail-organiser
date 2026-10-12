using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
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
        (await ApproveAsync(single, [Existing.ToLowerInvariant(), "extra"])).Changed.ShouldBe(1);
    }

    [Fact]
    public async Task Accepting_an_alternative_keeps_the_extras()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a01").ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraLabels, new[] { "Extra" }), Ct);
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
