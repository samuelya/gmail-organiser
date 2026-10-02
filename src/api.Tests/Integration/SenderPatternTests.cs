using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Apply to rest of sender over the harness mailbox: shop (a00–a09), news (b00–b05), billing (c00–c03).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SenderPatternTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string DeleteLabel = "Synthetic Delete";
    private const string Topic = "Example/Offers";
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
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
            .UpdateAsync(s => s with { ActionLabelName = "Synthetic Action", DeleteLabelName = DeleteLabel }, Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task The_pattern_is_the_most_approved_outcome_of_the_user()
    {
        await SeedAsync(
            ("a00", Topic, true, SuggestionStatus.Approved, SuggestionSource.Llm),
            ("a01", Topic, true, SuggestionStatus.Applied, SuggestionSource.Derived),
            ("a02", "Example/Other", false, SuggestionStatus.Approved, SuggestionSource.Llm),
            ("a03", "Example/Other", false, SuggestionStatus.Approved, SuggestionSource.SenderPattern),
            ("a04", "Example/Other", false, SuggestionStatus.Approved, SuggestionSource.SenderPattern),
            ("a05", "Example/Other", false, SuggestionStatus.Pending, SuggestionSource.Llm),
            ("a06", "Example/Other", false, SuggestionStatus.Rejected, SuggestionSource.Llm));

        var pattern = await PatternAsync(AnalysisRunHarness.Shop.ToUpperInvariant());

        pattern.ShouldBe(new SenderPatternDto(Topic, false, true, 3, 2.0 / 3, 3));
        (await PatternAsync(AnalysisRunHarness.News)).ShouldBe(new SenderPatternDto(null, null, null, 0, 0, 6));
    }

    [Fact]
    public async Task Apply_rest_creates_approved_suggestions_and_applies_only_them()
    {
        await SeedAsync(
            ("a00", Topic, true, SuggestionStatus.Approved, SuggestionSource.Llm),
            ("a01", Topic, true, SuggestionStatus.Approved, SuggestionSource.Llm),
            ("a02", Topic, true, SuggestionStatus.Applied, SuggestionSource.Llm),
            ("a03", "Example/Other", false, SuggestionStatus.Pending, SuggestionSource.Llm),
            ("a04", Topic, true, SuggestionStatus.Rejected, SuggestionSource.Llm));
        await StarAsync("a05");
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.FromAddress == AnalysisRunHarness.Shop)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ListId, "offers.example.com"), Ct);
        }

        var response = await h.PostAsync($"/api/review/senders/{AnalysisRunHarness.Shop}/apply-rest", new ApplyRestRequest());
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var result = (await response.Content.ReadFromJsonAsync<ApplyRestResponse>(Ct)).ShouldNotBeNull();
        (result.Created, result.ProtectedAdjusted).ShouldBe((5, 1));
        result.FilterCandidate.ShouldBe(new FilterCandidateDto(AnalysisRunHarness.Shop, "offers.example.com"));
        var batch = result.Batch.ShouldNotBeNull();
        (batch.Kind, batch.Description).ShouldBe(("apply_rest", $"Apply rest of {AnalysisRunHarness.Shop}: 5 messages"));

        await using (var db = postgres.CreateDbContext())
        {
            var created = await db.Suggestions.Where(s => s.Source == SuggestionSource.SenderPattern).ToListAsync(Ct);
            created.Select(s => s.MessageId).ShouldBe(["a05", "a06", "a07", "a08", "a09"], ignoreOrder: true);
            created.ShouldAllBe(s => s.Status == SuggestionStatus.Approved && s.TopicLabel == Topic
                && s.Confidence == 1 && s.Reason == SenderPatternService.Reason && !s.Edited);
            created.Single(s => s.MessageId == "a05").ToBeDeleted.ShouldBeFalse();
            created.Where(s => s.MessageId != "a05").ShouldAllBe(s => s.ToBeDeleted);
            var decision = await db.Decisions.SingleAsync(d => d.Source == SuggestionSource.SenderPattern, Ct);
            (decision.MessageId, decision.TopicLabel, decision.ToBeDeleted, decision.Outcome).ShouldBe(((string?)null, Topic, true, DecisionOutcome.Approved));
            (await db.Senders.SingleAsync(s => s.Address == AnalysisRunHarness.Shop, Ct)).AnalysedCount.ShouldBe(10);
        }

        await h.RunNextAsync();

        var deleteId = (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == DeleteLabel).Id;
        foreach (var id in new[] { "a05", "a06", "a07", "a08", "a09" })
        {
            Labels(id).ShouldNotContain("INBOX");
        }

        Labels("a05").ShouldNotContain(deleteId);
        Labels("a06").ShouldContain(deleteId);
        foreach (var id in new[] { "a00", "a01", "a03", "a04" })
        {
            Labels(id).ShouldContain("INBOX");
        }

        await using var after = postgres.CreateDbContext();
        (await after.Suggestions.SingleAsync(s => s.MessageId == "a03", Ct)).Status.ShouldBe(SuggestionStatus.Pending);
        (await after.Suggestions.SingleAsync(s => s.MessageId == "a04", Ct)).Status.ShouldBe(SuggestionStatus.Rejected);
        (await after.Suggestions.SingleAsync(s => s.MessageId == "a00", Ct)).Status.ShouldBe(SuggestionStatus.Approved);
        (await after.Suggestions.CountAsync(s => s.Source == SuggestionSource.SenderPattern && s.Status == SuggestionStatus.Applied, Ct)).ShouldBe(5);
        (await after.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct)).MessageCount.ShouldBe(5);
        (await PatternAsync(AnalysisRunHarness.Shop)).ShouldBe(new SenderPatternDto(Topic, false, true, 3, 1, 0));
    }

    [Fact]
    public async Task Without_a_pattern_a_topic_label_is_required()
    {
        var url = $"/api/review/senders/{AnalysisRunHarness.News}/apply-rest";
        (await h.PostAsync(url, new ApplyRestRequest(NeedsAction: true))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PostAsync(url, new ApplyRestRequest("INBOX"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.CountAsync(Ct)).ShouldBe(0);
            (await db.Decisions.CountAsync(Ct)).ShouldBe(0);
        }

        var response = await h.PostAsync(url, new ApplyRestRequest(" Example/News ", NeedsAction: true));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var result = (await response.Content.ReadFromJsonAsync<ApplyRestResponse>(Ct)).ShouldNotBeNull();
        (result.Created, result.ProtectedAdjusted, result.FilterCandidate.ListId).ShouldBe((6, 0, null));
        await using var after = postgres.CreateDbContext();
        var created = await after.Suggestions.ToListAsync(Ct);
        created.ShouldAllBe(s => s.TopicLabel == "Example/News" && s.NeedsAction && !s.ToBeDeleted && s.Confidence == 0);
    }

    [Fact]
    public async Task Nothing_remaining_returns_no_batch()
    {
        await SeedAsync([.. Enumerable.Range(0, 4).Select(i => ($"c{i:D2}", Topic, false, SuggestionStatus.Approved, SuggestionSource.Llm))]);

        var response = await h.PostAsync($"/api/review/senders/{AnalysisRunHarness.Billing}/apply-rest", new ApplyRestRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ApplyRestResponse>(Ct)).ShouldNotBeNull();
        (result.Created, result.Batch).ShouldBe((0, null));
        await using var db = postgres.CreateDbContext();
        (await db.ActionBatches.CountAsync(Ct)).ShouldBe(0);
        (await db.Decisions.CountAsync(Ct)).ShouldBe(0);
    }

    private async Task<SenderPatternDto> PatternAsync(string address)
    {
        var response = await h.GetAsync($"/api/review/senders/{address}/pattern");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SenderPatternDto>(Ct)).ShouldNotBeNull();
    }

    private async Task SeedAsync(params (string Id, string Topic, bool ToBeDeleted, SuggestionStatus Status, SuggestionSource Source)[] rows)
    {
        await using var db = postgres.CreateDbContext();
        var decidedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var row in rows)
        {
            var message = await db.Messages.SingleAsync(m => m.Id == row.Id, Ct);
            var suggestion = new SuggestionRow
            {
                Id = Guid.NewGuid(),
                MessageId = row.Id,
                SenderAddress = message.FromAddress,
                Source = row.Source,
                TopicLabel = row.Topic,
                ToBeDeleted = row.ToBeDeleted,
                Confidence = 0.9,
                Reason = "Synthetic reason",
                CreatedAt = decidedAt,
            };
            suggestion.SetStatus(row.Status, message, decidedAt);
            db.Suggestions.Add(suggestion);
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task StarAsync(string id)
    {
        string[] labels = ["INBOX", "CATEGORY_UPDATES", "STARRED"];
        h.Gmail.Inner.SetLabels(id, labels);
        await using var db = postgres.CreateDbContext();
        await db.Messages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, labels), Ct);
    }

    private IReadOnlyList<string> Labels(string id) => h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds;
}
