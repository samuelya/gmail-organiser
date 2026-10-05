using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Learned sub-rules (#361) over the harness mailbox: the shop's "Weekly offer n" mail (a00–a09) under an approved mixed
/// policy whose only approved rule matches none of it, and pending LLM suggestions for a00–a03.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LearnedRuleTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Topic = "Example/Offers";
    private static readonly DateTimeOffset Created = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly AnalysisRunHarness h = new(factory, postgres);
    private readonly Guid policyId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        await db.Messages.Where(m => m.FromAddress == AnalysisRunHarness.Shop).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.CanonicalAddress, AnalysisRunHarness.Shop)
            .SetProperty(m => m.CanonicalDomain, "example.com"), Ct);
        db.SenderPolicies.Add(new SenderPolicyRow
        {
            Id = policyId,
            Scope = PolicyScope.Sender,
            ScopeKey = AnalysisRunHarness.Shop,
            IsMixed = true,
            Action = PolicyAction.Keep,
            Confidence = 0.9,
            Reason = "synthetic",
            Status = PolicyStatus.Approved,
            CreatedAt = Created,
            Rules =
            [
                new SenderPolicyRuleRow
                {
                    Id = Guid.NewGuid(),
                    Name = "Synthetic rule",
                    Match = new RuleMatch { SubjectContains = "unrelated" },
                    TopicLabel = "Example/Other",
                    Action = PolicyAction.Archive,
                    Status = PolicyStatus.Approved,
                    Source = PolicyRuleSource.Llm,
                    Reason = "synthetic",
                    CreatedAt = Created,
                },
            ],
        });

        foreach (var message in await db.Messages.Where(m => string.Compare(m.Id, "a04") < 0 && m.Id.StartsWith("a")).ToListAsync(Ct))
        {
            var suggestion = new SuggestionRow
            {
                Id = Guid.NewGuid(),
                MessageId = message.Id,
                SenderAddress = message.FromAddress,
                Source = SuggestionSource.Llm,
                TopicLabel = Topic,
                ToBeDeleted = true,
                Confidence = 0.99,
                Reason = "Synthetic reason",
                CreatedAt = Created,
            };
            suggestion.SetStatus(SuggestionStatus.Pending, message, Created);
            db.Suggestions.Add(suggestion);
        }

        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Approving_two_suggestions_of_one_template_proposes_one_rule_that_the_policy_detail_lists()
    {
        await DecideAsync("a00", "approve");
        await DecideAsync("a01", "approve");

        var rule = (await LearnedAsync()).ShouldHaveSingleItem();
        rule.Match.SubjectTemplate.ShouldBe(SubjectNormaliser.Template("Weekly offer 1"));
        rule.Match.Category.ShouldBe(MessageCategory.Updates);
        (rule.Status, rule.Action, rule.TopicLabel, rule.Position, rule.Reason)
            .ShouldBe((PolicyStatus.Proposed, PolicyAction.Delete, Topic, 1, LearnedRuleProposer.LearnedReason));

        var detail = (await (await h.GetAsync($"/api/policies/{policyId}")).Content.ReadFromJsonAsync<SenderPolicyDetailDto>(Ct)).ShouldNotBeNull();
        detail.Rules.Count.ShouldBe(2);
        var listed = detail.Rules.Single(r => r.Id == rule.Id);
        (listed.Status, listed.Source).ShouldBe(("proposed", "learned"));
    }

    [Fact]
    public async Task A_bulk_approve_in_one_transaction_proposes_one_rule()
    {
        var response = await h.PostAsync("/api/review/bulk-approve", new BulkApproveRequest(SettingsValidation.MinBulkApproveThreshold));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<BulkApproveResponse>(Ct)).ShouldNotBeNull().Approved.ShouldBe(4);
        (await LearnedAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Rejecting_learns_nothing_and_neither_does_a_single_label_policy()
    {
        await DecideAsync("a00", "reject");
        (await LearnedAsync()).ShouldBeEmpty();

        await using (var db = postgres.CreateDbContext())
        {
            await db.SenderPolicies.Where(p => p.Id == policyId).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsMixed, false)
                .SetProperty(p => p.TopicLabel, "Example/Shop"), Ct);
        }

        await DecideAsync("a01", "approve");
        (await LearnedAsync()).ShouldBeEmpty();
    }

    private async Task DecideAsync(string messageId, string action)
    {
        Guid id;
        await using (var db = postgres.CreateDbContext())
        {
            id = await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
        }

        (await h.PostAsync($"/api/review/suggestions/{id}/{action}", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<List<SenderPolicyRuleRow>> LearnedAsync()
    {
        await using var db = postgres.CreateDbContext();
        return await db.SenderPolicyRules.AsNoTracking().Where(r => r.Source == PolicyRuleSource.Learned).ToListAsync(Ct);
    }
}
