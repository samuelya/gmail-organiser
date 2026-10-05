using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Claude;
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
    public async Task Concurrent_approvals_of_one_template_propose_one_rule()
    {
        await Task.WhenAll(new[] { "a00", "a01", "a02", "a03" }.Select(id => DecideAsync(id, "approve")));

        (await LearnedAsync()).ShouldHaveSingleItem().Position.ShouldBe(1);
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

    [Fact]
    public async Task An_empty_subject_learns_no_rule()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "a00").ExecuteUpdateAsync(s => s.SetProperty(m => m.Subject, "  "), Ct);
        }

        await DecideAsync("a00", "approve");
        (await LearnedAsync()).ShouldBeEmpty();

        await DecideAsync("a01", "approve");
        (await LearnedAsync()).ShouldHaveSingleItem().Match.SubjectTemplate.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Bulk_approves_reaching_two_policies_in_opposite_orders_both_succeed()
    {
        // A list policy and a domain policy; the shop's first approval teaches the list one, the other sender's the domain one.
        var listPolicy = Guid.NewGuid();
        var domainPolicy = Guid.NewGuid();
        await using (var db = postgres.CreateDbContext())
        {
            await db.SenderPolicies.Where(p => p.Id == policyId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PolicyStatus.Rejected), Ct);
            await db.Suggestions.ExecuteDeleteAsync(Ct);
            await db.Messages.Where(m => m.FromAddress == AnalysisRunHarness.Other).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.CanonicalAddress, AnalysisRunHarness.Other)
                .SetProperty(m => m.CanonicalDomain, "example.com"), Ct);
            await db.Messages.Where(m => m.Id == "a00" || m.Id == "x01")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ListId, "<offers.example.com>"), Ct);
            db.SenderPolicies.AddRange(
                Policy(listPolicy, PolicyScope.List, GroupKey.NormaliseListId("<offers.example.com>")!),
                Policy(domainPolicy, PolicyScope.Domain, "example.com"));
            var order = 0;
            foreach (var message in await db.Messages.Where(m => new[] { "a00", "a01", "x00", "x01" }.Contains(m.Id)).OrderBy(m => m.Id).ToListAsync(Ct))
            {
                var suggestion = new SuggestionRow
                {
                    Id = new Guid($"00000000-0000-0000-0000-{++order:D12}"),
                    MessageId = message.Id,
                    SenderAddress = message.FromAddress,
                    Source = SuggestionSource.Llm,
                    TopicLabel = Topic,
                    Confidence = 0.99,
                    Reason = "Synthetic reason",
                    CreatedAt = Created,
                };
                suggestion.SetStatus(SuggestionStatus.Pending, message, Created);
                db.Suggestions.Add(suggestion);
            }

            await db.SaveChangesAsync(Ct);
        }

        // Holding the list policy makes both requests wait with their suggestions locked: without one lock order the shop's
        // request then holds the list policy and waits for the domain one, which the other sender's request holds.
        Task<HttpResponseMessage>[] requests;
        await using (var holder = postgres.CreateDbContext())
        {
            await using var tx = await holder.Database.BeginTransactionAsync(Ct);
            await holder.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM sender_policies WHERE id = {listPolicy} FOR UPDATE").ToListAsync(Ct);
            requests =
            [
                h.PostAsync("/api/review/bulk-approve", new BulkApproveRequest(SettingsValidation.MinBulkApproveThreshold, SenderAddress: AnalysisRunHarness.Shop)),
                h.PostAsync("/api/review/bulk-approve", new BulkApproveRequest(SettingsValidation.MinBulkApproveThreshold, SenderAddress: AnalysisRunHarness.Other)),
            ];
            await h.WaitForLockWaitAsync(2);
            await tx.CommitAsync(Ct);
        }

        foreach (var response in await Task.WhenAll(requests))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<BulkApproveResponse>(Ct)).ShouldNotBeNull().Approved.ShouldBe(2);
        }

        (await LearnedAsync()).Select(r => r.PolicyId).Order().ShouldBe(new[] { listPolicy, listPolicy, domainPolicy, domainPolicy }.Order());
    }

    [Fact]
    public async Task A_claude_group_accept_over_one_chunk_and_an_overlapping_bulk_approve_both_succeed()
    {
        // More than one chunk in the group: the low ids at 0.5, a tail above the bulk threshold that the bulk approve locks.
        const string groupKey = "synthetic-group";
        const int members = ReviewService.ChunkSize + 200;
        var review = Guid.NewGuid();
        await using (var db = postgres.CreateDbContext())
        {
            await db.ExternalReviews.ExecuteDeleteAsync(Ct);
            await db.Suggestions.ExecuteDeleteAsync(Ct);
            for (var i = 1; i <= members; i++)
            {
                var message = new MessageRow
                {
                    Id = $"g{i:D5}",
                    ThreadId = $"g{i:D5}",
                    FromAddress = AnalysisRunHarness.Shop,
                    CanonicalAddress = AnalysisRunHarness.Shop,
                    CanonicalDomain = "example.com",
                    Subject = $"Weekly offer {i}",
                    InternalDate = Created,
                    LabelIds = ["INBOX"],
                    Category = MessageCategory.Updates,
                    FetchedAt = Created,
                    UpdatedAt = Created,
                };
                var suggestion = new SuggestionRow
                {
                    Id = new Guid($"00000000-0000-0000-0000-{i:D12}"),
                    MessageId = message.Id,
                    SenderAddress = AnalysisRunHarness.Shop,
                    GroupKey = groupKey,
                    Source = SuggestionSource.Llm,
                    TopicLabel = Topic,
                    Confidence = i <= ReviewService.ChunkSize ? 0.5 : 0.99,
                    Reason = "Synthetic reason",
                    CreatedAt = Created,
                };
                suggestion.SetStatus(SuggestionStatus.Pending, message, Created);
                db.Messages.Add(message);
                db.Suggestions.Add(suggestion);
            }

            db.ExternalReviews.Add(new ExternalReviewRow
            {
                Id = review,
                TargetType = ExternalReviewTarget.Group,
                SenderAddress = AnalysisRunHarness.Shop,
                GroupKey = groupKey,
                Status = ExternalReviewStatus.Reviewed,
                Verdict = ReviewVerdict.Agree,
                VerdictTopicLabel = Topic,
                VerdictNeedsAction = false,
                VerdictToBeDeleted = false,
                CreatedAt = Created,
            });
            await db.SaveChangesAsync(Ct);
        }

        // The accept queues on the held policy first, then the bulk approve behind it (#426): without one lock order the
        // accept holds the policy and waits for the tail the bulk approve holds while it waits for the policy.
        HttpResponseMessage accepted, bulk;
        await using (var holder = postgres.CreateDbContext())
        {
            await using var tx = await holder.Database.BeginTransactionAsync(Ct);
            await holder.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM sender_policies WHERE id = {policyId} FOR UPDATE").ToListAsync(Ct);
            var accept = h.PostAsync($"/api/claude/reviews/{review}/accept", new { });
            await h.WaitForLockWaitAsync();
            var approve = h.PostAsync("/api/review/bulk-approve", new BulkApproveRequest(0.95));
            await h.WaitForLockWaitAsync(2);
            await tx.CommitAsync(Ct);
            (accepted, bulk) = (await accept, await approve);
        }

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
        bulk.StatusCode.ShouldBe(HttpStatusCode.OK, await bulk.Content.ReadAsStringAsync(Ct));
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Approved, Ct)).ShouldBe(members);
        }

        (await LearnedAsync()).ShouldHaveSingleItem();
    }

    private static SenderPolicyRow Policy(Guid id, PolicyScope scope, string scopeKey) => new()
    {
        Id = id,
        Scope = scope,
        ScopeKey = scopeKey,
        IsMixed = true,
        Action = PolicyAction.Keep,
        Confidence = 0.9,
        Reason = "synthetic",
        Status = PolicyStatus.Approved,
        CreatedAt = Created,
    };

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
