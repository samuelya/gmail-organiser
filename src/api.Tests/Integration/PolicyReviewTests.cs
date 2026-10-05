using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The policy review endpoints (#358) over Postgres and the fake Gmail, on a synthetic mixed sender.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PolicyReviewTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Shop = "shop@example.com";
    private const string Bank = "bank@example.com";
    private const string Old = "old@example.com";
    private static readonly DateTimeOffset Newest = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid ShopPolicy = Guid.NewGuid();
    private static readonly Guid BankPolicy = Guid.NewGuid();
    private static readonly Guid OldPolicy = Guid.NewGuid();
    private static readonly Guid DealsRule = Guid.NewGuid();
    private static readonly Guid PromoRule = Guid.NewGuid();
    private static readonly Guid RejectedRule = Guid.NewGuid();

    private WebApplicationFactory<Program> host = null!;
    private HttpClient client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = 1 }), TimeProvider.System);
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), [], retry));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<FakeGmailClient>());
            services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
        }));
        client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
        await db.Senders.ExecuteDeleteAsync(Ct);
        await db.Settings.ExecuteDeleteAsync(Ct);

        var hour = 0;
        var rows = new List<MessageRow>();
        // 4 weekly deals (one with an attachment), 2 sales, 3 account news; one deleted deal is not live.
        for (var i = 0; i < 4; i++)
        {
            var deal = Row($"deal{i}", Shop, $"Weekly deal {i}", hour++, MessageCategory.Promotions);
            deal.HasAttachment = i == 3;
            rows.Add(deal);
        }

        rows.Add(Row("sale0", Shop, "Spring sale", hour++, MessageCategory.Promotions));
        rows.Add(Row("sale1", Shop, "Summer sale", hour++, MessageCategory.Promotions));
        rows.AddRange(Enumerable.Range(0, 3).Select(i => Row($"news{i}", Shop, $"Account news {i}", hour++, MessageCategory.Updates)));
        var gone = Row("gone", Shop, "Weekly deal 9", 0, MessageCategory.Promotions);
        gone.DeletedInGmail = true;
        rows.Add(gone);
        rows.Add(Row("bank0", Bank, "Monthly summary", hour++, MessageCategory.Updates));
        db.Messages.AddRange(rows);

        db.Senders.AddRange(Sender(Shop, total: 9, unread: 6), Sender(Bank, total: 20, unread: 0), Sender(Old, total: 1, unread: 1));

        var shop = Policy(ShopPolicy, Shop, PolicyStatus.Proposed);
        shop.IsMixed = true;
        shop.TopicLabel = null;
        shop.Action = PolicyAction.Keep;
        shop.Rules =
        [
            Rule(DealsRule, 0, PolicyStatus.Proposed, new RuleMatch { SubjectContains = "weekly deal" }, PolicyAction.Delete),
            Rule(PromoRule, 1, PolicyStatus.Approved, new RuleMatch { Category = MessageCategory.Promotions }, PolicyAction.Archive),
            Rule(RejectedRule, 2, PolicyStatus.Rejected, new RuleMatch { SubjectContains = "account" }, PolicyAction.Delete),
        ];
        db.SenderPolicies.AddRange(shop, Policy(BankPolicy, Bank, PolicyStatus.Proposed), Policy(OldPolicy, Old, PolicyStatus.Rejected));
        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await host.DisposeAsync();
    }

    [Fact]
    public async Task List_filters_by_status_and_search_and_sorts_by_message_count()
    {
        var proposed = await ListAsync("");
        proposed.Total.ShouldBe(2);
        proposed.Items.Select(p => p.ScopeKey).ShouldBe([Bank, Shop]);
        var shop = proposed.Items[1];
        shop.MessageCount.ShouldBe(9);
        shop.RuleCount.ShouldBe(3);
        shop.UnreadRatio.ShouldBe(Math.Round(6 / 9.0, 3));
        shop.Kind.ShouldBe("mixed");
        shop.Status.ShouldBe("proposed");

        (await ListAsync("?status=rejected")).Items.Select(p => p.ScopeKey).ShouldBe([Old]);
        (await ListAsync("?search=SHOP")).Items.Select(p => p.ScopeKey).ShouldBe([Shop]);
        (await ListAsync("?status=approved")).Total.ShouldBe(0);

        var bad = await client.GetAsync("/api/policies?status=maybe&pageSize=0", Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = (await bad.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct)).ShouldNotBeNull();
        problem.Errors.Keys.ShouldBe(["status", "pageSize"], ignoreOrder: true);
    }

    [Fact]
    public async Task Detail_counts_rules_in_saved_order_with_proposed_rules_as_approved()
    {
        var detail = await DetailAsync(ShopPolicy);

        detail.Rules.Select(r => r.Id).ShouldBe([DealsRule, PromoRule, RejectedRule]);
        // The proposed deals rule comes first and takes the deals; the attachment deal is guarded from its delete.
        detail.Rules[0].MatchCount.ShouldBe(3);
        detail.Rules[0].SampleSubjects.ShouldBe(["Weekly deal 0", "Weekly deal 1", "Weekly deal 2"]);
        detail.Rules[1].MatchCount.ShouldBe(2);
        detail.Rules[2].MatchCount.ShouldBe(0);
        detail.GuardedCount.ShouldBe(1);
        detail.UnmatchedCount.ShouldBe(3);
        detail.DefaultCount.ShouldBe(0);
        detail.SampledMessages.ShouldBe(9);
        detail.Sampled.ShouldBeFalse();
        detail.ProfileTemplates.ShouldNotBeEmpty();

        (await client.GetAsync($"/api/policies/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Edit_reorders_replaces_rules_and_returns_the_new_preview()
    {
        var request = new EditPolicyRequest(null, null, null, null, "keep", true,
        [
            RuleRequest(PromoRule, new RuleMatchDto(Category: "promotions"), "archive"),
            RuleRequest(DealsRule, new RuleMatchDto(SubjectContains: "weekly deal"), "delete"),
            RuleRequest(null, new RuleMatchDto(SubjectContains: "account news"), "keep"),
        ]);

        var response = await client.PutAsJsonAsync($"/api/policies/{ShopPolicy}", request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var edited = (await response.Content.ReadFromJsonAsync<EditPolicyResponse>(Ct)).ShouldNotBeNull();
        edited.Reapply.ShouldBeFalse();
        edited.Policy.Policy.Edited.ShouldBeTrue();
        var rules = edited.Policy.Rules;
        rules.Select(r => r.Position).ShouldBe([0, 1, 2]);
        rules[0].Id.ShouldBe(PromoRule);
        rules[0].MatchCount.ShouldBe(6);
        rules[1].MatchCount.ShouldBe(0);
        rules[1].Status.ShouldBe("proposed");
        rules[2].Source.ShouldBe("user");
        rules[2].MatchCount.ShouldBe(3);
        edited.Policy.UnmatchedCount.ShouldBe(0);

        await using var db = postgres.CreateDbContext();
        (await db.SenderPolicyRules.AnyAsync(r => r.Id == RejectedRule, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Edit_with_invalid_fields_is_a_400_and_saves_nothing()
    {
        var request = new EditPolicyRequest(null, null, "not_a_type", 0, "delete", true,
        [
            RuleRequest(Guid.NewGuid(), new RuleMatchDto(SubjectContains: "x"), "archive"),
            RuleRequest(null, new RuleMatchDto(), "archive"),
        ]);

        var response = await client.PutAsJsonAsync($"/api/policies/{ShopPolicy}", request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct)).ShouldNotBeNull();
        problem.Errors.Keys.ShouldBe(["mailType", "retentionDays", "rules[0].id"], ignoreOrder: true);

        // The mixed delete default and the empty match come from PolicyValidation once the fields parse.
        var second = await client.PutAsJsonAsync($"/api/policies/{ShopPolicy}",
            new EditPolicyRequest(null, null, null, null, "delete", true, [RuleRequest(null, new RuleMatchDto(), "archive")]), Ct);
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await second.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct))!.Errors.Keys
            .ShouldBe(["action", "rules[0].match"], ignoreOrder: true);

        await using var db = postgres.CreateDbContext();
        var stored = await db.SenderPolicies.Include(p => p.Rules).SingleAsync(p => p.Id == ShopPolicy, Ct);
        stored.Edited.ShouldBeFalse();
        stored.Rules.Count.ShouldBe(3);

        var rejected = await client.PutAsJsonAsync($"/api/policies/{OldPolicy}",
            new EditPolicyRequest("Example/Topic", null, null, null, "archive", false, []), Ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approve_reject_rule_decisions_and_delete_follow_the_transitions()
    {
        var approved = await PostAsync<ApprovePolicyResponse>($"/api/policies/{ShopPolicy}/approve");
        approved.Policy.Status.ShouldBe("approved");
        approved.Policy.DecidedAt.ShouldNotBeNull();
        approved.JobId.ShouldBeNull();
        (await PostStatusAsync($"/api/policies/{ShopPolicy}/approve")).ShouldBe(HttpStatusCode.Conflict);
        (await PostStatusAsync($"/api/policies/{ShopPolicy}/reject")).ShouldBe(HttpStatusCode.Conflict);

        var detail = await DetailAsync(ShopPolicy);
        detail.Rules.Select(r => r.Status).ShouldBe(["approved", "approved", "rejected"]);

        var afterRule = await PostAsync<SenderPolicyDetailDto>($"/api/policies/{ShopPolicy}/rules/{DealsRule}/reject");
        afterRule.Rules[0].Status.ShouldBe("rejected");
        afterRule.Rules[1].MatchCount.ShouldBe(6);
        (await PostStatusAsync($"/api/policies/{ShopPolicy}/rules/{DealsRule}/reject")).ShouldBe(HttpStatusCode.Conflict);
        (await PostStatusAsync($"/api/policies/{ShopPolicy}/rules/{Guid.NewGuid()}/approve")).ShouldBe(HttpStatusCode.NotFound);

        var edit = await client.PutAsJsonAsync($"/api/policies/{ShopPolicy}",
            new EditPolicyRequest(null, null, null, null, "archive", true, [RuleRequest(PromoRule, new RuleMatchDto(Category: "promotions"), "archive")]), Ct);
        (await edit.Content.ReadFromJsonAsync<EditPolicyResponse>(Ct))!.Reapply.ShouldBeTrue();

        (await client.DeleteAsync($"/api/policies/{BankPolicy}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostAsync<SenderPolicyDto>($"/api/policies/{BankPolicy}/reject")).Status.ShouldBe("rejected");
        (await PostStatusAsync($"/api/policies/{BankPolicy}/rules/{Guid.NewGuid()}/approve")).ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/policies/{BankPolicy}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/policies/{BankPolicy}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<PagedDto<SenderPolicyDto>> ListAsync(string query) =>
        (await client.GetFromJsonAsync<PagedDto<SenderPolicyDto>>($"/api/policies{query}", Ct)).ShouldNotBeNull();

    private async Task<SenderPolicyDetailDto> DetailAsync(Guid id) =>
        (await client.GetFromJsonAsync<SenderPolicyDetailDto>($"/api/policies/{id}", Ct)).ShouldNotBeNull();

    private async Task<T> PostAsync<T>(string path)
        where T : class
    {
        var response = await client.PostAsync(path, null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }

    private async Task<HttpStatusCode> PostStatusAsync(string path) => (await client.PostAsync(path, null, Ct)).StatusCode;

    private static EditPolicyRuleRequest RuleRequest(Guid? id, RuleMatchDto match, string action) =>
        new(id, "Synthetic rule", match, "Example/Shop", null, null, null, action);

    private static MessageRow Row(string id, string from, string subject, int hoursAgo, MessageCategory category) => new()
    {
        Id = id,
        ThreadId = "t-" + id,
        FromAddress = from,
        CanonicalAddress = from,
        CanonicalDomain = "example.com",
        Subject = subject,
        InternalDate = Newest.AddHours(-hoursAgo),
        LabelIds = [],
        Category = category,
        FetchedAt = Newest,
        UpdatedAt = Newest,
    };

    private static SenderRow Sender(string address, int total, int unread) => new()
    {
        Address = address,
        Domain = "example.com",
        CanonicalAddress = address,
        CanonicalDomain = "example.com",
        TotalCount = total,
        UnreadCount = unread,
        Kind = address == Shop ? SenderKind.Mixed : SenderKind.Unknown,
        UpdatedAt = Newest,
    };

    private static SenderPolicyRow Policy(Guid id, string address, PolicyStatus status) => new()
    {
        Id = id,
        Scope = PolicyScope.Sender,
        ScopeKey = address,
        TopicLabel = "Example/Topic",
        Action = PolicyAction.Archive,
        Confidence = 0.9,
        Reason = "synthetic",
        Status = status,
        CreatedAt = Newest,
    };

    private static SenderPolicyRuleRow Rule(Guid id, int position, PolicyStatus status, RuleMatch match, PolicyAction action) => new()
    {
        Id = id,
        Position = position,
        Name = $"Synthetic rule {position}",
        Match = match,
        TopicLabel = "Example/Shop",
        Action = action,
        Status = status,
        Source = PolicyRuleSource.Llm,
        Reason = "synthetic",
        CreatedAt = Newest,
    };
}
