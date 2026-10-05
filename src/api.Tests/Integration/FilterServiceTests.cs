using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Review;
using GmailOrganiser.Rules;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Filter preview, create, delete, restore and proposals over a small mailbox: shop (s0–s3, s0 with an attachment),
/// news (n0–n1, a mailing list), billing (b0–b2). The fake's seed filters include <c>from:news@example.com</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FilterServiceTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Shop = "shop@example.com";
    private const string News = "news@example.com";
    private const string Billing = "billing@example.com";
    private const string DeleteLabel = "Synthetic Delete";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeGmailClient Gmail => host.Services.GetRequiredService<FakeGmailClient>();

    public async ValueTask InitializeAsync()
    {
        await postgres.ResetFetchStateAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.ActionLog.ExecuteDeleteAsync(Ct);
            await db.ActionBatches.ExecuteDeleteAsync(Ct);
            await db.Filters.ExecuteDeleteAsync(Ct);
            await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(r => r.FiltersSyncedAt, (DateTimeOffset?)null), Ct);
            await db.Suggestions.ExecuteDeleteAsync(Ct);
            await db.Messages.ExecuteDeleteAsync(Ct);
            await db.Senders.ExecuteDeleteAsync(Ct);
            await db.Settings.ExecuteDeleteAsync(Ct);
            db.Messages.AddRange(Mailbox().Select(m => new MessageRow
            {
                Id = m.Id,
                ThreadId = m.ThreadId,
                FromAddress = m.From,
                Subject = m.Subject,
                ToHeader = m.To,
                ListId = m.ListId,
                InternalDate = m.Date,
                LabelIds = [.. m.LabelIds],
                HasAttachment = m.HasAttachment,
                FetchedAt = Now,
                UpdatedAt = Now,
            }));
            db.Senders.AddRange(Mailbox().GroupBy(m => m.From).Select(g => new SenderRow
            {
                Address = g.Key,
                Domain = "example.com",
                DisplayName = $"Synthetic {g.Key[..g.Key.IndexOf('@')]}",
                TotalCount = g.Count(),
                UpdatedAt = Now,
            }));
            await db.SaveChangesAsync(Ct);
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            // One attempt, so an injected rate limit fails the request without real backoff delays.
            var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = 1 }), TimeProvider.System);
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), Mailbox(), retry));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<FakeGmailClient>());
        }));
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { DeleteLabelName = DeleteLabel }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task Preview_counts_locally_asks_gmail_for_an_estimate_and_lists_labels_to_create()
    {
        var preview = await PreviewAsync(new FilterCriteriaDto(Shop, null, null, null, null, true, null, null, null),
            new FilterActionRequest(["Example", "Synthetic Filters/Shop"], SkipInbox: true, MarkRead: false));

        preview.Query.ShouldBe($"from:{Shop} has:attachment");
        preview.CriteriaSummary.ShouldBe($"from:{Shop} has:attachment");
        (preview.LocalMatches, preview.GmailEstimate).ShouldBe((1, 1L));
        preview.Warnings.ShouldBeEmpty();
        preview.CreatesLabels.ShouldBe(["Synthetic Filters", "Synthetic Filters/Shop"]);
        preview.Action.AddLabels.Select(l => l.Id).ShouldBe(["Label_1", ""]);
        preview.Action.RemoveLabelIds.ShouldBe(["INBOX"]);
    }

    [Fact]
    public async Task Preview_evaluates_an_or_list_a_domain_and_a_negated_attachment_like_gmail()
    {
        var orList = await PreviewAsync(new FilterCriteriaDto($"{Shop} OR {News}", null, null, null, "has:attachment", null, null, null, null));
        var domain = await PreviewAsync(new FilterCriteriaDto("@example.com", null, null, null, null, null, null, null, null));

        orList.Query.ShouldBe($"from:({Shop} OR {News}) -has:attachment");
        (orList.LocalMatches, orList.GmailEstimate).ShouldBe((5, 5L));
        (domain.LocalMatches, domain.GmailEstimate).ShouldBe((9, 9L));
    }

    [Fact]
    public async Task Preview_counts_subject_to_and_list_criteria_locally()
    {
        var preview = await PreviewAsync(new FilterCriteriaDto(null, "LISTS@", "issue", "list:news.example.com", null, null, null, null, null));

        preview.LocalMatches.ShouldBe(2);
        preview.Query.ShouldBe("to:LISTS@ subject:issue list:news.example.com");
    }

    [Fact]
    public async Task A_criterion_without_a_local_equivalent_gives_no_local_count_and_a_warning()
    {
        var preview = await PreviewAsync(new FilterCriteriaDto(null, null, null, "in:inbox", null, null, null, null, null));

        preview.LocalMatches.ShouldBeNull();
        preview.GmailEstimate.ShouldBe(9);
        preview.Warnings.ShouldHaveSingleItem().ShouldContain("list:<id>");
    }

    [Fact]
    public async Task A_rate_limited_estimate_is_a_warning_not_a_failed_preview()
    {
        Gmail.FailNext(HttpStatusCode.TooManyRequests, 1);

        var preview = await PreviewAsync(new FilterCriteriaDto(Shop, null, null, null, null, null, null, null, null));

        preview.LocalMatches.ShouldBe(4);
        preview.GmailEstimate.ShouldBeNull();
        preview.Warnings.ShouldHaveSingleItem().ShouldContain("rate-limiting");
    }

    [Fact]
    public async Task An_estimate_far_from_the_local_count_explains_the_difference()
    {
        Gmail.AddMessage(new FakeMessage("s9", "t-s9", Shop, "Not fetched yet", Now, ["INBOX"]));
        Gmail.AddMessage(new FakeMessage("s8", "t-s8", Shop, "Not fetched yet", Now, ["INBOX"]));

        var preview = await PreviewAsync(new FilterCriteriaDto(Shop, null, null, null, null, null, null, null, null));

        (preview.LocalMatches, preview.GmailEstimate).ShouldBe((4, 6L));
        preview.Warnings.ShouldHaveSingleItem().ShouldContain("Spam, Trash");
    }

    [Fact]
    public async Task Create_syncs_first_creates_the_label_and_the_filter_and_logs_the_row()
    {
        var response = await PostAsync("/api/rules/filters", JsonContent.Create(new CreateFilterRequest(
            new FilterCriteriaDto($"  {Shop} ", null, null, null, null, null, null, null, null),
            new FilterActionRequest(["Synthetic Filters/Shop"], SkipInbox: true, MarkRead: true))));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var filter = (await response.Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull();
        filter.CreatedByApp.ShouldBeTrue();
        filter.Criteria.From.ShouldBe(Shop);
        (filter.Action.SkipInbox, filter.Action.MarkRead).ShouldBe((true, true));
        var label = (await Gmail.ListLabelsAsync(Ct)).Single(l => l.Name == "Synthetic Filters/Shop");
        filter.Action.AddLabels.ShouldBe([new LabelRefDto(label.Id, "Synthetic Filters/Shop")]);
        var created = (await Gmail.ListFiltersAsync(Ct)).Single(f => f.Id == filter.Id);
        created.Criteria.ShouldBe(new GmailFilterCriteria(From: Shop));
        created.Action.RemoveLabelIds.ShouldBe(["INBOX", "UNREAD"]);

        var list = await host.CreateClient().GetFromJsonAsync<FilterListDto>("/api/rules/filters", Ct);
        list.ShouldNotBeNull().SyncedAt.ShouldNotBeNull();
        list.ActiveCount.ShouldBe(FakeFilterStore.Seed.Count + 1);
        await using var db = postgres.CreateDbContext();
        var batch = await db.ActionBatches.SingleAsync(Ct);
        batch.Kind.ShouldBe(ActionKind.FilterLabels);
        batch.CreatedLabelIds.ShouldBe([(await Gmail.ListLabelsAsync(Ct)).Single(l => l.Name == "Synthetic Filters").Id, label.Id]);
    }

    [Fact]
    public async Task A_filter_gmail_already_has_is_409_with_gmails_reason()
    {
        (await CreateAsync(Shop)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await CreateAsync(Shop);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Filter already exists");
        await using var db = postgres.CreateDbContext();
        (await db.Filters.CountAsync(r => r.DeletedAt == null && r.CreatedByApp, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Create_is_409_at_gmails_filter_limit()
    {
        await SyncAsync();
        await using (var db = postgres.CreateDbContext())
        {
            db.Filters.AddRange(Enumerable.Range(0, FilterSnapshot.GmailFilterLimit - FakeFilterStore.Seed.Count).Select(i => new FilterRow
            {
                Id = $"synthetic-{i}",
                Criteria = FilterRow.WriteCriteria(new GmailFilterCriteria(From: $"s{i}@example.com")),
                CriteriaSummary = "synthetic",
                FirstSeenAt = Now,
                LastSeenAt = Now,
                UpdatedAt = Now,
            }));
            await db.SaveChangesAsync(Ct);
        }

        var response = await PostAsync("/api/rules/filters", JsonContent.Create(new CreateFilterRequest(
            new FilterCriteriaDto(Shop, null, null, null, null, null, null, null, null),
            new FilterActionRequest(["Synthetic Limit"], SkipInbox: true, MarkRead: false))));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Filter limit reached");
        (await Gmail.ListFiltersAsync(Ct)).Count.ShouldBe(FakeFilterStore.Seed.Count);
        (await Gmail.ListLabelsAsync(Ct)).ShouldNotContain(l => l.Name == "Synthetic Limit");
    }

    [Fact]
    public async Task Delete_then_restore_recreates_the_filter_under_a_new_id()
    {
        var original = (await (await CreateAsync(Shop)).Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull();

        (await DeleteAsync(original.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await DeleteAsync(original.Id)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DeleteAsync("unknown-filter")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Gmail.ListFiltersAsync(Ct)).ShouldNotContain(f => f.Id == original.Id);
        await using (var db = postgres.CreateDbContext())
        {
            var row = await db.Filters.SingleAsync(r => r.Id == original.Id, Ct);
            row.DeletedAt.ShouldNotBeNull();
            row.DeletedByApp.ShouldBeTrue();
        }

        var response = await PostAsync($"/api/rules/filters/{original.Id}/restore", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var restored = (await response.Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull();
        restored.Id.ShouldNotBe(original.Id);
        restored.RestoredFrom.ShouldBe(original.Id);
        restored.CreatedByApp.ShouldBeTrue();
        restored.Criteria.ShouldBe(original.Criteria);
        (await Gmail.ListFiltersAsync(Ct)).ShouldContain(f => f.Id == restored.Id);
        (await PostAsync($"/api/rules/filters/{original.Id}/restore", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync($"/api/rules/filters/{restored.Id}/restore", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Concurrent_restores_of_one_filter_create_one()
    {
        var original = (await (await CreateAsync(Shop)).Content.ReadFromJsonAsync<FilterDto>(Ct)).ShouldNotBeNull();
        (await DeleteAsync(original.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => PostAsync($"/api/rules/filters/{original.Id}/restore", null)));

        responses.Select(r => r.StatusCode).Order().ShouldBe([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        (await Gmail.ListFiltersAsync(Ct)).Count(f => f.Criteria.From == Shop).ShouldBe(1);
    }

    [Fact]
    public async Task Restore_is_409_naming_a_label_that_no_longer_exists()
    {
        await SyncAsync();
        (await DeleteAsync("fake-filter-3")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await PostAsync("/api/rules/filters/fake-filter-3/restore", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(FakeFilterStore.MissingLabelId);
    }

    [Fact]
    public async Task Mutations_are_503_when_gmail_is_not_connected()
    {
        await SyncAsync();
        await host.Services.GetRequiredService<FakeTokenStore>().DeleteAsync(Ct);

        (await CreateAsync(Shop)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await DeleteAsync("fake-filter-1")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    public static TheoryData<string, string> InvalidRequests => new()
    {
        { """{"criteria":{},"action":{"addLabelNames":["Example"]}}""", "criteria" },
        { """{"criteria":{"from":"   ","excludeChats":true},"action":{"addLabelNames":["Example"]}}""", "criteria" },
        { """{"criteria":{"from":"a@example.com"},"action":{"addLabelNames":[]}}""", "action" },
        { """{"criteria":{"from":"a@example.com"},"action":{"addLabelNames":["INBOX"]}}""", "action.addLabelNames" },
        { """{"criteria":{"from":"a@example.com"},"action":{"addLabelNames":["  "],"skipInbox":true}}""", "action.addLabelNames" },
        { """{"criteria":{"from":"a@example.com","size":10,"sizeComparison":"bigger"},"action":{"skipInbox":true}}""", "criteria.sizeComparison" },
        { """{"criteria":{"from":"a@example.com","size":10},"action":{"skipInbox":true}}""", "criteria.size" },
        { """{"action":{"skipInbox":true}}""", "criteria" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_requests_are_400_on_preview_and_create(string body, string field)
    {
        foreach (var path in new[] { "/api/rules/filters/preview", "/api/rules/filters" })
        {
            var response = await PostAsync(path, JsonContent.Create(System.Text.Json.JsonDocument.Parse(body).RootElement));

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain($"\"{field}\"");
        }

        (await Gmail.ListFiltersAsync(Ct)).Count.ShouldBe(FakeFilterStore.Seed.Count);
    }

    [Fact]
    public async Task Proposals_come_from_approved_senders_without_a_filter()
    {
        await SeedSuggestionsAsync(
            ("s0", "Synthetic/Offers", false, false, SuggestionStatus.Approved),
            ("s1", "Synthetic/Offers", false, false, SuggestionStatus.Applied),
            ("n0", "Synthetic/News", false, false, SuggestionStatus.Approved),
            ("b0", "Synthetic/Bills", true, true, SuggestionStatus.Approved),
            ("b1", "Synthetic/Bills", true, true, SuggestionStatus.Approved));
        await SyncAsync();

        var page = (await host.CreateClient().GetFromJsonAsync<PagedDto<FilterProposalDto>>("/api/rules/filters/proposals", Ct))
            .ShouldNotBeNull();

        page.Total.ShouldBe(2);
        page.Items.Select(p => p.SenderAddress).ShouldBe([Shop, Billing]);
        page.Items.Select(p => p.Key).ShouldBe([$"sender:{Shop}", $"sender:{Billing}"]);
        var shop = page.Items[0];
        (shop.MessageCount, shop.DisplayName, shop.Pattern.TopicLabel).ShouldBe((4, "Synthetic shop", "Synthetic/Offers"));
        shop.Suggested.Criteria.From.ShouldBe(Shop);
        shop.Suggested.Criteria.NegatedQuery.ShouldBeNull();
        shop.Suggested.Action.ShouldBe(new FilterActionRequest(["Synthetic/Offers"], SkipInbox: true, MarkRead: false), new ActionComparer());
        var billing = page.Items[1];
        billing.Suggested.Criteria.NegatedQuery.ShouldBe("has:attachment");
        billing.Suggested.Action.AddLabelNames.ShouldBe([DeleteLabel]);
        billing.Suggested.Action.SkipInbox.ShouldBeTrue();
    }

    [Fact]
    public async Task Proposals_hide_senders_a_filter_matches_by_address_or_domain_and_spare_allowlisted_senders_the_delete_label()
    {
        await SeedSuggestionsAsync(
            ("s0", "Synthetic/Offers", false, false, SuggestionStatus.Approved),
            ("b0", "Synthetic/Bills", true, true, SuggestionStatus.Approved));
        await SyncAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Senders.Where(s => s.Address == Billing).ExecuteUpdateAsync(u => u.SetProperty(s => s.Allowlisted, true), Ct);
            db.Filters.Add(SyntheticFilter("suffix", $"(x{Shop} OR @example.org)"));
            await db.SaveChangesAsync(Ct);
        }

        var page = await ProposalsAsync();

        page.Items.Select(p => p.SenderAddress).ShouldBe([Shop, Billing]);
        var billing = page.Items[1].Suggested;
        billing.Criteria.NegatedQuery.ShouldBeNull();
        billing.Action.ShouldBe(new FilterActionRequest(["Synthetic/Bills"], SkipInbox: false, MarkRead: false), new ActionComparer());

        await using (var db = postgres.CreateDbContext())
        {
            db.Filters.Add(SyntheticFilter("domain", "@example.com"));
            await db.SaveChangesAsync(Ct);
        }

        (await ProposalsAsync()).Total.ShouldBe(0);
    }

    [Fact]
    public async Task A_sender_on_an_allowlisted_domain_is_spared_the_delete_label_too()
    {
        await SeedSuggestionsAsync(("b0", "Synthetic/Bills", true, true, SuggestionStatus.Approved));
        await SyncAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
                .UpdateAsync(s => s with { Protection = s.Protection with { AllowlistedDomains = ["example.com"] } }, Ct);
        }

        var billing = (await ProposalsAsync()).Items.Single(p => p.SenderAddress == Billing).Suggested;

        billing.Criteria.NegatedQuery.ShouldBeNull();
        billing.Action.ShouldBe(new FilterActionRequest(["Synthetic/Bills"], SkipInbox: false, MarkRead: false), new ActionComparer());
    }

    [Fact]
    public async Task Proposals_page_size_is_capped()
    {
        (await host.CreateClient().GetAsync("/api/rules/filters/proposals?pageSize=101", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await host.CreateClient().GetAsync("/api/rules/filters/proposals?page=0", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static List<FakeMessage> Mailbox() =>
    [
        new("s0", "t-s0", Shop, "Weekly offer 1", Now.AddHours(-1), ["INBOX"],
            Attachments: [new FakeAttachment("att-1", "offer.pdf", "application/pdf", 3, [1, 2, 3])]),
        .. Enumerable.Range(1, 3).Select(i => new FakeMessage($"s{i}", $"t-s{i}", Shop, $"Weekly offer {i + 1}", Now.AddHours(-1 - i), ["INBOX"])),
        .. Enumerable.Range(0, 2).Select(i => new FakeMessage(
            $"n{i}", $"t-n{i}", News, $"Newsletter issue {i + 1}", Now.AddHours(-10 - i), ["INBOX"], To: "lists@example.com",
            ListId: "news.example.com")),
        .. Enumerable.Range(0, 3).Select(i => new FakeMessage($"b{i}", $"t-b{i}", Billing, $"Invoice {i + 1}", Now.AddHours(-20 - i), ["INBOX"])),
    ];

    private async Task SeedSuggestionsAsync(params (string Id, string Topic, bool NeedsAction, bool ToBeDeleted, SuggestionStatus Status)[] rows)
    {
        await using var db = postgres.CreateDbContext();
        foreach (var row in rows)
        {
            var message = await db.Messages.SingleAsync(m => m.Id == row.Id, Ct);
            var suggestion = new SuggestionRow
            {
                Id = Guid.NewGuid(),
                MessageId = row.Id,
                SenderAddress = message.FromAddress,
                Source = SuggestionSource.Llm,
                TopicLabel = row.Topic,
                NeedsAction = row.NeedsAction,
                ToBeDeleted = row.ToBeDeleted,
                Confidence = 0.9,
                Reason = "Synthetic reason",
                CreatedAt = Now,
            };
            suggestion.SetStatus(row.Status, message, Now);
            db.Suggestions.Add(suggestion);
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<FilterPreviewDto> PreviewAsync(FilterCriteriaDto criteria, FilterActionRequest? action = null)
    {
        var response = await PostAsync("/api/rules/filters/preview", JsonContent.Create(
            new FilterPreviewRequest(criteria, action ?? new FilterActionRequest(["Example"], SkipInbox: false, MarkRead: false))));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<FilterPreviewDto>(Ct)).ShouldNotBeNull();
    }

    private Task<HttpResponseMessage> CreateAsync(string from) => PostAsync("/api/rules/filters", JsonContent.Create(new CreateFilterRequest(
        new FilterCriteriaDto(from, null, null, null, null, null, null, null, null),
        new FilterActionRequest(["Example"], SkipInbox: true, MarkRead: false))));

    private static FilterRow SyntheticFilter(string id, string from) => new()
    {
        Id = id,
        Criteria = FilterRow.WriteCriteria(new GmailFilterCriteria(From: from)),
        CriteriaSummary = "synthetic",
        FirstSeenAt = Now,
        LastSeenAt = Now,
        UpdatedAt = Now,
    };

    private async Task<PagedDto<FilterProposalDto>> ProposalsAsync() =>
        (await host.CreateClient().GetFromJsonAsync<PagedDto<FilterProposalDto>>("/api/rules/filters/proposals", Ct)).ShouldNotBeNull();

    private async Task SyncAsync() =>
        (await PostAsync("/api/rules/filters/sync", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

    private Task<HttpResponseMessage> DeleteAsync(string id) => Client().DeleteAsync($"/api/rules/filters/{id}", Ct);

    private Task<HttpResponseMessage> PostAsync(string path, HttpContent? content) => Client().PostAsync(path, content, Ct);

    private HttpClient Client()
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    private sealed class ActionComparer : IEqualityComparer<FilterActionRequest>
    {
        public bool Equals(FilterActionRequest? x, FilterActionRequest? y) =>
            x is not null && y is not null && x.SkipInbox == y.SkipInbox && x.MarkRead == y.MarkRead
            && (x.AddLabelNames ?? []).SequenceEqual(y.AddLabelNames ?? []);

        public int GetHashCode(FilterActionRequest obj) => HashCode.Combine(obj.SkipInbox, obj.MarkRead);
    }
}
