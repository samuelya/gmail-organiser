using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Prompts;
using GmailOrganiser.Rules.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The LLM summary of a stored filter review, with the fake chat client; one stored message holds text that must never reach the prompt.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FilterReviewSummaryTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string ChatModel = "fake-summary-model";
    private const string Snippet = "synthetic snippet text that stays out of prompts";
    private const string Subject = "Synthetic private subject";
    private const string FilterId = "summary-filter-1";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly FakeChatClient chat = new();
    private WebApplicationFactory<Program> host = null!;
    private Guid reviewId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        reviewId = Guid.CreateVersion7();
        await using (var db = postgres.CreateDbContext())
        {
            await db.FilterReviews.ExecuteDeleteAsync(Ct);
            await db.Filters.ExecuteDeleteAsync(Ct);
            await db.Messages.ExecuteDeleteAsync(Ct);
            await db.Settings.ExecuteDeleteAsync(Ct);
            db.Messages.Add(new MessageRow
            {
                Id = "m1", ThreadId = "t1", FromAddress = "news@example.com", Subject = Subject, Snippet = Snippet,
                InternalDate = Now.AddDays(-1), LabelIds = ["INBOX"], FetchedAt = Now, UpdatedAt = Now,
            });
            var criteria = new GmailFilterCriteria(From: "news@example.com");
            db.Filters.Add(new FilterRow
            {
                Id = FilterId, Criteria = FilterRow.WriteCriteria(criteria), Action = FilterRow.WriteAction(new GmailFilterAction(["Label_1"], [])),
                CriteriaSummary = FilterSnapshot.Summarise(criteria), FirstSeenAt = Now, LastSeenAt = Now, UpdatedAt = Now,
            });
            db.FilterReviews.AddRange(
                new FilterReviewRow { Id = reviewId, CreatedAt = Now, FilterCount = 1, FindingCount = 1 },
                new FilterReviewRow { Id = Guid.CreateVersion7(), CreatedAt = Now.AddMinutes(-1), FilterCount = 1 });
            var finding = new FilterFindingRow
            {
                Id = Guid.CreateVersion7(), ReviewId = reviewId, Kind = FilterFindingKind.NoRecentMatches, FilterIds = [FilterId],
                Description = "No message from the last 90 days matches this filter.", Status = FilterFindingStatus.Open,
            };
            finding.WriteFix(new FilterFix(FilterFixKind.Delete, [FilterId]));
            db.FilterFindings.Add(finding);
            await db.SaveChangesAsync(Ct);
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
            services.AddScoped<ILlmClientFactory>(_ => new FakeLlmClientFactory(chat))));
        await SetChatModelAsync(ChatModel);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task A_summary_is_stored_with_the_configured_model_and_the_prompt_holds_no_message_text()
    {
        chat.Enqueue("  Delete the unused filter first.\u0007 It is safe.\r\n  ");

        var review = await SummariseAsync(HttpStatusCode.OK);

        review.Summary.ShouldBe("Delete the unused filter first. It is safe.");
        (review.SummaryModel, review.SummaryPromptVersion, review.SummaryError).ShouldBe((ChatModel, RulesSummaryPromptBuilder.Version, null));
        review.SummarisedAt.ShouldNotBeNull();
        var stored = await Client().GetFromJsonAsync<FilterReviewDto>($"/api/rules/filters/reviews/{reviewId}", Ct);
        stored.ShouldNotBeNull().Summary.ShouldBe(review.Summary);

        var request = chat.Requests.ShouldHaveSingleItem();
        (request.Options?.Temperature, request.Options?.MaxOutputTokens).ShouldBe((0.2f, 600));
        var prompt = string.Join('\n', request.Messages.Select(m => m.Text));
        prompt.ShouldContain("from:news@example.com");
        prompt.ShouldContain("kind: no_recent_matches");
        prompt.ShouldNotContain(Snippet);
        prompt.ShouldNotContain(Subject);
        request.Messages[0].Text.ShouldNotContain("news@example.com");
    }

    [Fact]
    public async Task A_failed_call_records_the_error_and_keeps_the_previous_summary()
    {
        chat.Enqueue("First summary of the findings.");
        var first = await SummariseAsync(HttpStatusCode.OK);
        chat.Failure = new HttpRequestException("Connection refused");

        var failed = await SummariseAsync(HttpStatusCode.OK);

        failed.SummaryError.ShouldNotBeNull().ShouldContain("Connection refused");
        (failed.Summary, failed.SummaryModel, failed.SummarisedAt).ShouldBe((first.Summary, first.SummaryModel, first.SummarisedAt));

        chat.Failure = null;
        chat.Enqueue("Second summary.");
        var again = await SummariseAsync(HttpStatusCode.OK);
        (again.Summary, again.SummaryError).ShouldBe(("Second summary.", null));
    }

    [Fact]
    public async Task An_empty_answer_is_a_recorded_failure()
    {
        chat.Enqueue(" \u0001\n ");

        var review = await SummariseAsync(HttpStatusCode.OK);

        (review.Summary, review.SummarisedAt).ShouldBe((null, null));
        review.SummaryError.ShouldNotBeNull();
    }

    [Fact]
    public async Task Unknown_reviews_reviews_without_findings_and_a_missing_model_are_refused()
    {
        (await PostAsync($"/api/rules/filters/reviews/{Guid.NewGuid()}/summary")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using (var db = postgres.CreateDbContext())
        {
            var empty = await db.FilterReviews.Where(r => r.Id != reviewId).Select(r => r.Id).SingleAsync(Ct);
            (await PostAsync($"/api/rules/filters/reviews/{empty}/summary")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        await SetChatModelAsync(null);
        (await PostAsync($"/api/rules/filters/reviews/{reviewId}/summary")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        chat.Requests.ShouldBeEmpty();
    }

    private async Task<FilterReviewDto> SummariseAsync(HttpStatusCode expected)
    {
        var response = await PostAsync($"/api/rules/filters/reviews/{reviewId}/summary");
        response.StatusCode.ShouldBe(expected);
        return (await response.Content.ReadFromJsonAsync<FilterReviewDto>(Ct)).ShouldNotBeNull();
    }

    private async Task SetChatModelAsync(string? model)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { ChatModel = model }, Ct);
    }

    private Task<HttpResponseMessage> PostAsync(string path) => Client().PostAsync(path, null, Ct);

    private HttpClient Client()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }
}
