using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
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

/// <summary>
/// Filter proposals from approved sender policies (#373) over synthetic senders, policies and filters: label-only by
/// default, never the delete label, skip inbox only for an exact sender policy that archives or deletes.
/// </summary>
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
    public async Task A_list_policy_proposes_a_label_only_list_query_until_a_filter_covers_it()
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
        proposal.Suggested.Action.SkipInbox.ShouldBeFalse();
        (proposal.Partial, proposal.RuleId).ShouldBe((false, null));
        proposal.Note.ShouldNotBeNull().ShouldContain("Labels only: a narrower policy");

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
        shop.Note.ShouldNotBeNull().ShouldContain("keeps the mail in the inbox");
        items.Single(p => p.SenderAddress == "example.org").Suggested.Criteria.From.ShouldBe("@example.org OR x3@relay.example.net");
    }

    [Fact]
    public async Task A_mixed_policy_proposes_a_label_only_filter_per_rule_whatever_its_order_or_outcome()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("offers@example.com", "offers@example.com"));
            var policy = Policy(PolicyScope.Sender, "offers@example.com", PolicyAction.Archive, null);
            policy.IsMixed = true;
            policy.Rules =
            [
                Rule(policy.Id, 0, new RuleMatch { SubjectContains = "Security alert" }, PolicyAction.Keep),
                Rule(policy.Id, 1, new RuleMatch { SubjectContains = "Daily deal" }, PolicyAction.Delete),
                Rule(policy.Id, 2, new RuleMatch { Category = MessageCategory.Promotions }, PolicyAction.Archive),
            ];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items;

        // No first-match exclusions: a rule's filter never deletes or archives, so overlapping filters only add labels.
        items.Select(p => p.Suggested.Criteria.Query).ShouldBe(
            ["subject:\"Security alert\"", "subject:\"Daily deal\"", "category:promotions"], ignoreOrder: true);
        items.ShouldAllBe(p => p.RuleId != null && p.Suggested.Criteria.From == "offers@example.com");
        items.ShouldAllBe(p => !p.Suggested.Action.SkipInbox && p.Pattern.ToBeDeleted == false);
        items.ShouldAllBe(p => p.Suggested.Action.AddLabelNames!.SequenceEqual(new[] { "Synthetic/Topic" }));
        items.ShouldAllBe(p => p.Note!.Contains("Labels only: Gmail can't apply a mixed policy"));
    }

    [Fact]
    public async Task An_exact_sender_archive_or_delete_policy_skips_the_inbox_without_the_delete_label_and_with_every_keyword_excluded()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.AddRange(Sender("deals@example.com", "deals@example.com"), Sender("bulk@example.com", "bulk@example.com"));
            db.SenderPolicies.AddRange(
                Policy(PolicyScope.Sender, "deals@example.com", PolicyAction.Delete, null),
                Policy(PolicyScope.Sender, "bulk@example.com", PolicyAction.Archive, null));
            await db.SaveChangesAsync(Ct);
        }

        // The same outcome: one merged filter. Important, replied or allowlisted mail is safe because no delete label is added.
        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        Keywords().Count.ShouldBeGreaterThan(8);
        proposal.Suggested.Criteria.From.ShouldBe("bulk@example.com OR deals@example.com");
        proposal.Suggested.Criteria.Query.ShouldBe(Negation());
        proposal.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Topic"]);
        proposal.Suggested.Action.SkipInbox.ShouldBeTrue();
        proposal.Pattern.ToBeDeleted.ShouldBe(false);
        proposal.Note.ShouldNotBeNull().ShouldContain("Skips the inbox");
    }

    [Fact]
    public async Task No_proposal_adds_the_delete_label_even_when_it_is_the_topic()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.AddRange(
                Sender("junk@example.com", "junk@example.com"), Sender("keep@example.com", "keep@example.com"), Sender("mixed@example.com", "mixed@example.com"));
            var mixed = Policy(PolicyScope.Sender, "mixed@example.com", PolicyAction.Keep, null);
            mixed.IsMixed = true;
            mixed.Rules = [Rule(mixed.Id, 0, new RuleMatch { Category = MessageCategory.Promotions }, PolicyAction.Delete, DeleteLabel.ToUpperInvariant())];
            db.SenderPolicies.AddRange(
                Policy(PolicyScope.Sender, "junk@example.com", PolicyAction.Delete, DeleteLabel),
                Policy(PolicyScope.Sender, "keep@example.com", PolicyAction.Keep, DeleteLabel),
                mixed);
            await db.SaveChangesAsync(Ct);
        }

        // Only the delete policy has something left to do: leave the inbox. The others would do nothing.
        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        proposal.SenderAddress.ShouldBe("junk@example.com");
        proposal.Suggested.Action.AddLabelNames.ShouldBeEmpty();
        proposal.Suggested.Action.SkipInbox.ShouldBeTrue();
        proposal.Pattern.ToBeDeleted.ShouldBe(false);
        proposal.Note.ShouldNotBeNull().ShouldContain("Doesn't add the delete label");
    }

    [Fact]
    public async Task Action_mail_keeps_the_inbox_and_a_too_long_keyword_negation_only_labels()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.AddRange(Sender("bills@example.com", "bills@example.com"), Sender("bulk@example.com", "bulk@example.com"));
            var bills = Policy(PolicyScope.Sender, "bills@example.com", PolicyAction.Archive, "Synthetic/Bills");
            bills.MailType = MailType.ActionBill;
            db.SenderPolicies.AddRange(bills, Policy(PolicyScope.Sender, "bulk@example.com", PolicyAction.Delete, null));
            await db.SaveChangesAsync(Ct);
        }

        await using var many = host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.PostConfigure<PolicyOptions>(o =>
            o.TransactionalKeywords = [.. Enumerable.Range(0, 200).Select(i => $"syntheticword{i:D3}")])));

        var items = (await ProposalsAsync("policy", many)).Items;

        items.Count.ShouldBe(2);
        items.ShouldAllBe(p => !p.Suggested.Action.SkipInbox && p.Suggested.Criteria.Query == null && p.Pattern.ToBeDeleted == false);
        var billsProposal = items.Single(p => p.SenderAddress == "bills@example.com");
        billsProposal.Pattern.NeedsAction.ShouldBe(true);
        billsProposal.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Bills"]);
        billsProposal.Note.ShouldNotBeNull().ShouldContain("needs action");
        items.Single(p => p.SenderAddress == "bulk@example.com").Note.ShouldNotBeNull().ShouldContain("too long");
    }

    [Fact]
    public async Task When_automatic_policy_apply_on_fetched_mail_is_off_no_filter_skips_the_inbox()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("deals@example.com", "deals@example.com"));
            db.SenderPolicies.Add(Policy(PolicyScope.Sender, "deals@example.com", PolicyAction.Delete, null));
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { PolicyAutoApplyFetched = false }, Ct);
        }

        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        proposal.Suggested.Action.SkipInbox.ShouldBeFalse();
        proposal.Suggested.Criteria.Query.ShouldBeNull();
        proposal.Note.ShouldNotBeNull().ShouldContain("is off");
        proposal.Note.ShouldContain("new mail goes to review");
        proposal.Note.ShouldNotContain("fetches");
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
        longs.ShouldAllBe(p => FilterCriteriaMapping.ToQuery(new GmailFilterCriteria(p.Suggested.Criteria.From, null, null, p.Suggested.Criteria.Query, null, null, null, null, null))
            .Length <= FilterCriteriaLimits.MaxQueryChars);
        items.ShouldAllBe(p => p.Suggested.Action.SkipInbox && p.Note!.StartsWith("Merges the filters of", StringComparison.Ordinal));
        items.ShouldAllBe(p => p.RuleId == null);
    }

    [Fact]
    public async Task A_rule_with_a_condition_gmail_cannot_test_is_partial_and_one_with_none_gets_no_filter()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("promo@example.com", "promo@example.com"));
            var policy = Policy(PolicyScope.Sender, "promo@example.com", PolicyAction.Keep, null);
            policy.IsMixed = true;
            policy.Rules =
            [
                Rule(policy.Id, 0, new RuleMatch { ListUnsubscribePresent = true, Category = MessageCategory.Updates }, PolicyAction.Archive),
                Rule(policy.Id, 1, new RuleMatch { ListUnsubscribePresent = true }, PolicyAction.Delete),
                Rule(policy.Id, 2, new RuleMatch { SubjectContains = "--" }, PolicyAction.Delete),
            ];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        proposal.DisplayName.ShouldBe("Synthetic rule 0");
        proposal.Partial.ShouldBeTrue();
        proposal.Note.ShouldNotBeNull().ShouldContain("List-Unsubscribe");
        proposal.Suggested.Criteria.Query.ShouldBe("category:updates");
        proposal.Suggested.Action.SkipInbox.ShouldBeFalse();
        proposal.Pattern.ToBeDeleted.ShouldBe(false);
    }

    [Fact]
    public async Task A_punctuation_only_subject_text_is_a_dropped_condition()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("promo@example.com", "promo@example.com"));
            var policy = Policy(PolicyScope.Sender, "promo@example.com", PolicyAction.Keep, null);
            policy.IsMixed = true;
            policy.Rules = [Rule(policy.Id, 0, new RuleMatch { SubjectContains = "--", Category = MessageCategory.Updates }, PolicyAction.Delete)];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        proposal.Partial.ShouldBeTrue();
        proposal.Note.ShouldNotBeNull().ShouldContain("\"--\"");
        proposal.Suggested.Criteria.Query.ShouldBe("category:updates");
        proposal.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Topic"]);
    }

    [Fact]
    public async Task A_single_label_policy_proposes_only_its_default_and_ignores_leftover_rules()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("news@example.com", "news@example.com"));
            var policy = Policy(PolicyScope.Sender, "news@example.com", PolicyAction.Keep, "Synthetic/News");
            policy.Rules = [Rule(policy.Id, 0, new RuleMatch { SubjectContains = "Weekly" }, PolicyAction.Delete)];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        proposal.RuleId.ShouldBeNull();
        proposal.Pattern.ToBeDeleted.ShouldBe(false);
        proposal.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/News"]);
        proposal.Suggested.Action.SkipInbox.ShouldBeFalse();
    }

    [Fact]
    public async Task A_subject_template_rule_is_partial_whether_or_not_it_has_placeholders()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("orders@example.com", "orders@example.com"));
            var policy = Policy(PolicyScope.Sender, "orders@example.com", PolicyAction.Keep, null);
            policy.IsMixed = true;
            policy.Rules =
            [
                Rule(policy.Id, 0, new RuleMatch { SubjectTemplate = "Order # shipped" }, PolicyAction.Delete),
                Rule(policy.Id, 1, new RuleMatch { SubjectTemplate = "your weekly digest" }, PolicyAction.Delete),
            ];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items;

        items.Select(p => p.Suggested.Criteria.Query).ShouldBe(["subject:\"shipped\"", "subject:\"your weekly digest\""], ignoreOrder: true);
        items.ShouldAllBe(p => p.Partial && p.Pattern.ToBeDeleted == false && !p.Suggested.Action.SkipInbox);
        items.ShouldAllBe(p => p.Note!.Contains("not by the template"));
    }

    [Fact]
    public async Task A_rule_from_condition_narrows_the_policy_senders_and_a_mixed_default_gets_no_filter()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(Sender("news@mail.example.com", "news@mail.example.com"));
            var policy = Policy(PolicyScope.Sender, "news@mail.example.com", PolicyAction.Delete, null);
            policy.IsMixed = true;
            policy.Rules =
            [
                Rule(policy.Id, 0, new RuleMatch { FromSubdomain = "mail.example.com", Category = MessageCategory.Promotions }, PolicyAction.Delete),
            ];
            db.SenderPolicies.Add(policy);
            await db.SaveChangesAsync(Ct);
        }

        var proposal = (await ProposalsAsync("policy")).Items.ShouldHaveSingleItem();

        proposal.Suggested.Criteria.From.ShouldBe("news@mail.example.com");
        proposal.Suggested.Criteria.Query.ShouldBe("from:@mail.example.com category:promotions");
        proposal.Pattern.ToBeDeleted.ShouldBe(false);
    }

    [Fact]
    public async Task Domain_and_list_filters_are_label_only_and_exclude_narrower_policies_and_known_subdomains_even_unseen_on_them()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.AddRange(
                Sender("billing@example.com", "billing@example.com"),
                Sender("promo@example.com", "promo@example.com"),
                Sender("alerts@news.example.com", "alerts@news.example.com"));
            // The billing sender has never posted to the list, and no domain mail has been on it.
            db.Messages.Add(Message("m1", "digest@example.org", ListId));
            db.SenderPolicies.AddRange(
                Policy(PolicyScope.Domain, "example.com", PolicyAction.Delete, "Synthetic/Domain"),
                Policy(PolicyScope.Sender, "billing@example.com", PolicyAction.Keep, "Synthetic/Billing"),
                Policy(PolicyScope.List, ListId, PolicyAction.Delete, "Synthetic/Digest"));
            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items;

        var domain = items.Single(p => p.SenderAddress == "example.com");
        domain.Suggested.Criteria.From.ShouldBe("@example.com");
        domain.Suggested.Criteria.Query.ShouldBe($"-from:@news.example.com -from:billing@example.com -list:{ListId}");
        domain.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Domain"]);
        domain.Suggested.Action.SkipInbox.ShouldBeFalse();
        domain.Pattern.ToBeDeleted.ShouldBe(false);
        domain.Note.ShouldNotBeNull().ShouldContain("Labels only: a narrower policy");
        domain.Note.ShouldContain("known subdomains");
        var list = items.Single(p => p.SenderAddress == ListId);
        list.Suggested.Criteria.Query.ShouldBe($"list:{ListId} -from:billing@example.com");
        list.Suggested.Action.SkipInbox.ShouldBeFalse();
        list.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Digest"]);
    }

    [Fact]
    public async Task A_mixed_domain_policy_rule_filter_excludes_narrower_policies_too()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.AddRange(Sender("shop@example.org", "shop@example.org"), Sender("news@example.org", "news@example.org"));
            var policy = Policy(PolicyScope.Domain, "example.org", PolicyAction.Keep, null);
            policy.IsMixed = true;
            policy.Rules = [Rule(policy.Id, 0, new RuleMatch { Category = MessageCategory.Promotions }, PolicyAction.Archive, "Synthetic/Shopping")];
            db.SenderPolicies.AddRange(policy, Policy(PolicyScope.Sender, "shop@example.org", PolicyAction.Keep, "Synthetic/Finance"));
            await db.SaveChangesAsync(Ct);
        }

        var items = (await ProposalsAsync("policy")).Items;

        var rule = items.Single(p => p.RuleId != null);
        rule.Suggested.Criteria.From.ShouldBe("@example.org");
        rule.Suggested.Criteria.Query.ShouldBe("-from:shop@example.org category:promotions");
        rule.Suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Shopping"]);
        rule.Suggested.Action.SkipInbox.ShouldBeFalse();
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

    private List<string> Keywords() => host.Services.GetRequiredService<IOptions<PolicyOptions>>().Value.TransactionalKeywords;

    private string Negation() => string.Join(' ', Keywords().Select(k => "-" + k).Prepend("-has:attachment"));

    private async Task<PagedDto<FilterProposalDto>> ProposalsAsync(string source, WebApplicationFactory<Program>? app = null) =>
        (await (app ?? host).CreateClient().GetFromJsonAsync<PagedDto<FilterProposalDto>>($"/api/rules/filters/proposals?source={source}", Ct))
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
        CanonicalAddress = from,
        CanonicalDomain = from[(from.IndexOf('@') + 1)..],
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

    private static SenderPolicyRuleRow Rule(Guid policyId, int position, RuleMatch match, PolicyAction action, string topic = "Synthetic/Topic") => new()
    {
        Id = Guid.NewGuid(),
        PolicyId = policyId,
        Position = position,
        Name = $"Synthetic rule {position}",
        Match = match,
        TopicLabel = topic,
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
