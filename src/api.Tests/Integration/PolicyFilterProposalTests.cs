using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Policies;
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

/// <summary>Filter proposals from approved sender policies (#373) over synthetic senders, policies and filters.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PolicyFilterProposalTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string DeleteLabel = "Synthetic Delete";
    private const string ListId = "digest.example.com";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await postgres.ResetFetchStateAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.ActionLog.ExecuteDeleteAsync(Ct);
            await db.ActionBatches.ExecuteDeleteAsync(Ct);
            await db.Filters.ExecuteDeleteAsync(Ct);
            await db.SenderPolicies.ExecuteDeleteAsync(Ct);
            await db.Suggestions.ExecuteDeleteAsync(Ct);
            await db.Messages.ExecuteDeleteAsync(Ct);
            await db.Senders.ExecuteDeleteAsync(Ct);
            await db.Settings.ExecuteDeleteAsync(Ct);
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true"));
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { DeleteLabelName = DeleteLabel }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(CancellationToken.None);
        await db.Settings.ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_list_policy_proposes_a_list_query_until_a_filter_covers_it()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Messages.AddRange(Enumerable.Range(0, 3).Select(i => Message($"m{i}", "digest@example.com", ListId)));
            db.SenderPolicies.Add(Policy(PolicyScope.List, ListId, PolicyAction.Archive, "Synthetic/Digest"));
            await db.SaveChangesAsync(Ct);
        }

        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        (proposal.Source, proposal.SenderAddress, proposal.ListId, proposal.MessageCount).ShouldBe(("policy", ListId, ListId, 3));
        proposal.Suggested.Criteria.From.ShouldBeNull();
        proposal.Suggested.Criteria.Query.ShouldBe($"list:{ListId}");
        proposal.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Digest"]);
        proposal.Suggested.Action.SkipInbox.ShouldBeTrue();
        (proposal.Partial, proposal.Note, proposal.RuleId).ShouldBe((false, null, null));

        await using (var db = postgres.CreateDbContext())
        {
            db.Filters.Add(Filter("list", new GmailFilterCriteria(Query: $"list:<{ListId}>")));
            await db.SaveChangesAsync(Ct);
        }

        (await ProposalsAsync("policy")).Total.ShouldBe(0);
    }

    [Fact]
    public async Task A_relay_sender_is_matched_by_its_raw_addresses_and_a_domain_by_its_domain_plus_relays()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.AddRange(
                Sender("x1@relay.example.net", "shop@example.com"),
                Sender("x2@relay.example.net", "shop@example.com"),
                Sender("news@example.org", "news@example.org"),
                Sender("x3@relay.example.net", "alerts@example.org"));
            db.SenderPolicies.AddRange(
                Policy(PolicyScope.Sender, "shop@example.com", PolicyAction.Keep, "Synthetic/Shop"),
                Policy(PolicyScope.Domain, "example.org", PolicyAction.Keep, "Synthetic/Org"));
            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items;

        var shop = items.Single(p => p.SenderAddress == "shop@example.com");
        shop.Suggested.Criteria.From.ShouldBe("x1@relay.example.net OR x2@relay.example.net");
        shop.Suggested.Action.SkipInbox.ShouldBeFalse();
        items.Single(p => p.SenderAddress == "example.org").Suggested.Criteria.From.ShouldBe("@example.org OR x3@relay.example.net");
    }

    [Fact]
    public async Task A_mixed_policy_proposes_a_filter_per_rule_and_its_delete_rule_excludes_transactional_mail()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("offers@example.com", "offers@example.com"));
            var policy = Policy(PolicyScope.Sender, "offers@example.com", PolicyAction.Archive, null);
            policy.IsMixed = true;
            policy.Rules =
            [
                Rule(policy.Id, 0, new RuleMatch { SubjectContains = "Daily deal" }, PolicyAction.Delete),
                Rule(policy.Id, 1, new RuleMatch { Category = MessageCategory.Promotions }, PolicyAction.Archive),
            ];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items;

        items.Count.ShouldBe(2);
        items.ShouldAllBe(p => p.RuleId != null && p.Suggested.Criteria.From == "offers@example.com");
        var keywords = host.Services.GetRequiredService<IOptions<PolicyOptions>>().Value.TransactionalKeywords;
        var delete = items.Single(p => p.Pattern.ToBeDeleted == true);
        delete.Suggested.Criteria.Query.ShouldBe(
            $"subject:\"Daily deal\" -has:attachment -subject:({string.Join(" OR ", keywords.Take(PolicyFilterProposalQuery.MaxNegatedKeywords))})");
        delete.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Topic", DeleteLabel]);
        delete.Suggested.Action.SkipInbox.ShouldBeTrue();
        var archive = items.Single(p => p.Pattern.ToBeDeleted == false);
        archive.Suggested.Criteria.Query.ShouldBe("category:promotions");
        archive.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Topic"]);
    }

    [Fact]
    public async Task Policies_with_the_same_outcome_merge_within_the_term_and_length_caps()
    {
        await using (var db = postgres.CreateDbContext())
        {
            var shortOnes = Enumerable.Range(0, 25).Select(i => $"s{i:D2}@example.com");
            var longOnes = Enumerable.Range(0, 20).Select(i => $"{new string('a', 60)}{i:D2}@{new string('b', 20)}.example.net");
            foreach (var (address, topic) in shortOnes.Select(a => (a, "Synthetic/Short")).Concat(longOnes.Select(a => (a, "Synthetic/Long"))))
            {
                db.Senders.Add(Sender(address, address));
                db.SenderPolicies.Add(Policy(PolicyScope.Sender, address, PolicyAction.Archive, topic));
            }

            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items;

        var terms = items.Where(p => p.Pattern.TopicLabel == "Synthetic/Short")
            .Select(p => FilterCriteriaMapping.FromTerms(p.Suggested.Criteria.From!)!.Count).ToList();
        terms.ShouldBe([20, 5], ignoreOrder: true);
        var longs = items.Where(p => p.Pattern.TopicLabel == "Synthetic/Long").ToList();
        longs.Count.ShouldBeGreaterThan(1);
        longs.Sum(p => FilterCriteriaMapping.FromTerms(p.Suggested.Criteria.From!)!.Count).ShouldBe(20);
        longs.ShouldAllBe(p => FilterCriteriaMapping.ToQuery(new GmailFilterCriteria(p.Suggested.Criteria.From, null, null, null, null, null, null, null, null))
            .Length <= FilterCriteriaLimits.MaxQueryChars);
        items.ShouldAllBe(p => p.Note == null || p.Note.StartsWith("Merges the filters of", StringComparison.Ordinal));
        items.ShouldAllBe(p => p.RuleId == null);
    }

    [Fact]
    public async Task A_rule_with_a_condition_gmail_cannot_test_is_partial_and_never_marks_for_deletion()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("promo@example.com", "promo@example.com"));
            var policy = Policy(PolicyScope.Sender, "promo@example.com", PolicyAction.Keep, null);
            policy.IsMixed = true;
            policy.Rules = [Rule(policy.Id, 0, new RuleMatch { ListUnsubscribePresent = true }, PolicyAction.Delete)];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        proposal.Partial.ShouldBeTrue();
        proposal.Note.ShouldNotBeNull().ShouldContain("List-Unsubscribe");
        proposal.Suggested.Criteria.Query.ShouldBeNull();
        proposal.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Topic"]);
        proposal.Suggested.Action.SkipInbox.ShouldBeTrue();
        proposal.Pattern.ToBeDeleted.ShouldBe(false);
    }

    [Fact]
    public async Task Source_picks_the_kind_and_rejects_unknown_values()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("shop@example.com", "shop@example.com"));
            db.SenderPolicies.Add(Policy(PolicyScope.Sender, "shop@example.com", PolicyAction.Archive, "Synthetic/Shop"));
            await db.SaveChangesAsync(Ct);
        }

        (await ProposalsAsync("all")).Items.ShouldHaveSingleItem().Source.ShouldBe("policy");
        (await ProposalsAsync("pattern")).Total.ShouldBe(0);
        (await host.CreateClient().GetAsync("/api/rules/filters/proposals?source=other", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<PagedDto<FilterProposalDto>> ProposalsAsync(string source) =>
        (await host.CreateClient().GetFromJsonAsync<PagedDto<FilterProposalDto>>($"/api/rules/filters/proposals?source={source}", Ct))
            .ShouldNotBeNull();

    private static SenderRow Sender(string address, string canonical) => new()
    {
        Address = address,
        Domain = address[(address.IndexOf('@') + 1)..],
        CanonicalAddress = canonical,
        CanonicalDomain = canonical[(canonical.IndexOf('@') + 1)..],
        TotalCount = 2,
        UpdatedAt = Now,
    };

    private static MessageRow Message(string id, string from, string? listId) => new()
    {
        Id = id,
        ThreadId = $"t-{id}",
        FromAddress = from,
        Subject = "Synthetic subject",
        ListId = listId,
        InternalDate = Now,
        LabelIds = ["INBOX"],
        FetchedAt = Now,
        UpdatedAt = Now,
    };

    private static SenderPolicyRow Policy(PolicyScope scope, string key, PolicyAction action, string? topic) => new()
    {
        Id = Guid.NewGuid(),
        Scope = scope,
        ScopeKey = key,
        TopicLabel = topic ?? "Synthetic/Topic",
        Action = action,
        Confidence = 0.9,
        Reason = "Synthetic reason",
        Status = PolicyStatus.Approved,
        CreatedAt = Now,
    };

    private static SenderPolicyRuleRow Rule(Guid policyId, int position, RuleMatch match, PolicyAction action) => new()
    {
        Id = Guid.NewGuid(),
        PolicyId = policyId,
        Position = position,
        Name = $"Synthetic rule {position}",
        Match = match,
        TopicLabel = "Synthetic/Topic",
        Action = action,
        Status = PolicyStatus.Approved,
        Source = PolicyRuleSource.User,
        Reason = "Synthetic reason",
        CreatedAt = Now,
    };

    private static FilterRow Filter(string id, GmailFilterCriteria criteria) => new()
    {
        Id = id,
        Criteria = FilterRow.WriteCriteria(criteria),
        CriteriaSummary = "synthetic",
        FirstSeenAt = Now,
        LastSeenAt = Now,
        UpdatedAt = Now,
    };
}
