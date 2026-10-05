using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Memory;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Policy apply (#359) over the harness mailbox (every message starts in INBOX) and the fake Gmail, two messages per
/// chunk so every walk spans several checkpoints.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PolicyApplyJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string ActionLabel = "Synthetic Action/Open";
    private const string DeleteLabel = "Synthetic Delete";
    private const string Topic = "Example/Shop";
    private const string Kept = "Example/Kept";
    private const string News = "Example/News";

    private readonly AnalysisRunHarness h = new(factory, postgres)
    {
        ConfigureServices = s => s.Configure<PolicyOptions>(o => o.ApplyChunkSize = 2),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ActionLog.ExecuteDeleteAsync(Ct);
            await db.ActionBatches.ExecuteDeleteAsync(Ct);
            await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        }

        await h.InitializeAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.ExecuteUpdateAsync(u => u.SetProperty(m => m.CanonicalAddress, m => m.FromAddress), Ct);
        }

        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
            .UpdateAsync(s => s with { ActionLabelName = ActionLabel, DeleteLabelName = DeleteLabel }, Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Approving_a_mixed_policy_applies_matched_mail_supersedes_pending_and_undo_restores()
    {
        // Shop: "Weekly offer 1..10" (a00..a09). Delete rule hits a00 and a09 ("offer 1", "offer 10"), keep rule a01,
        // archive rule a02; a03..a08 match nothing. a09 is starred (protected); a00 has a pending LLM suggestion;
        // a02 an approved one, which the policy leaves alone.
        var policy = await SeedPolicyAsync(AnalysisRunHarness.Shop, isMixed: true, PolicyAction.Archive,
            ("Deals", "offer 1", PolicyAction.Delete), ("Keep", "offer 2", PolicyAction.Keep), ("Archive", "offer 3", PolicyAction.Archive));
        await StarAsync("a09");
        await SeedSuggestionAsync("a00", SuggestionStatus.Pending);
        await SeedSuggestionAsync("a02", SuggestionStatus.Approved);

        var approved = await PostAsync<ApprovePolicyResponse>($"/api/policies/{policy}/approve");
        var jobId = approved.JobId.ShouldNotBeNull();
        (await h.PostWithoutBodyAsync($"/api/policies/{policy}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await h.RunNextAsync();
        h.Progress(jobId)[^1].Message.ShouldBe("Matched 3, superseded 1, unmatched 6, skipped 1 (rejected 0, edited 0)");

        // The walk only queued the apply: nothing has reached Gmail yet.
        (await LabelsAsync("a00")).ShouldContain("INBOX");
        await h.RunNextAsync();

        await using (var db = postgres.CreateDbContext())
        {
            var suggestions = await db.Suggestions.AsNoTracking().ToDictionaryAsync(s => s.MessageId, Ct);
            foreach (var id in new[] { "a00", "a01", "a09" })
            {
                var s = suggestions[id];
                (s.Source, s.Status, s.PolicyId, s.TopicLabel).ShouldBe((SuggestionSource.Policy, SuggestionStatus.Applied, policy, Topic));
            }

            (suggestions["a00"].ToBeDeleted, suggestions["a00"].Reason).ShouldBe((true, "Policy: Deals"));
            suggestions["a09"].ToBeDeleted.ShouldBeFalse();
            suggestions["a09"].Reason.ShouldStartWith("Policy: Deals; archived, not deleted (protected:");
            suggestions["a01"].KeepInInbox.ShouldBeTrue();
            (suggestions["a02"].Source, suggestions["a02"].Status).ShouldBe((SuggestionSource.Llm, SuggestionStatus.Approved));
            suggestions.Keys.Order().ShouldBe(["a00", "a01", "a02", "a09"]);
            (await db.Messages.CountAsync(m => m.FromAddress == AnalysisRunHarness.Shop && m.AnalysisStatus == AnalysisStatus.NotAnalysed, Ct))
                .ShouldBe(6);
            (await db.Decisions.Where(d => d.Source == SuggestionSource.Policy).Select(d => d.MessageId).OrderBy(m => m).ToListAsync(Ct))
                .ShouldBe(["a00", "a01", "a09"]);
            (await db.SenderPolicies.SingleAsync(p => p.Id == policy, Ct)).AppliedAt.ShouldNotBeNull();
        }

        (await LabelsAsync("a00")).ShouldBe([DeleteLabel, Topic], ignoreOrder: true);
        (await LabelsAsync("a09")).ShouldBe(["STARRED", Topic], ignoreOrder: true);
        (await LabelsAsync("a01")).ShouldBe(["INBOX", Topic], ignoreOrder: true);
        (await LabelsAsync("a02")).ShouldContain("INBOX");
        (await LabelsAsync("a03")).ShouldContain("INBOX");

        var batch = await BatchAsync(policy);
        (await h.PostAsync($"/api/history/{batch}/undo", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();
        (await LabelsAsync("a00")).ShouldContain("INBOX");
        (await LabelsAsync("a00")).ShouldNotContain(DeleteLabel);
        (await LabelsAsync("a09")).ShouldNotContain(Topic);
    }

    [Fact]
    public async Task Restart_mid_walk_resumes_without_duplicates_and_the_guard_wins()
    {
        // News: "Newsletter issue 1..6" (b00..b05), delete by default; b00 has an attachment (transactional).
        var policy = await SeedPolicyAsync(AnalysisRunHarness.News, isMixed: false, PolicyAction.Delete);
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "b00").ExecuteUpdateAsync(u => u.SetProperty(m => m.HasAttachment, true), Ct);
        }

        var jobId = (await PostAsync<ApprovePolicyResponse>($"/api/policies/{policy}/approve")).JobId.ShouldNotBeNull();
        using var stop = new CancellationTokenSource();
        h.OnPublish = job =>
        {
            // The host dies right after the second chunk commits.
            if (job.Id == jobId && job.Progress?.Done == 4)
            {
                stop.Cancel();
            }

            return Task.CompletedTask;
        };
        await h.RunNextAsync(stop.Token);
        h.OnPublish = null;
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();
        await h.RunNextAsync();

        await using (var db = postgres.CreateDbContext())
        {
            var rows = await db.Suggestions.AsNoTracking().Where(s => s.PolicyId == policy).ToListAsync(Ct);
            rows.Count.ShouldBe(6);
            rows.ShouldAllBe(s => s.Status == SuggestionStatus.Applied && s.TopicLabel == News);
            rows.Single(s => s.MessageId == "b00").ToBeDeleted.ShouldBeFalse();
            rows.Single(s => s.MessageId == "b00").Reason.ShouldBe("Policy: default; transactional mail is never deleted");
            (await db.Decisions.CountAsync(d => d.Source == SuggestionSource.Policy, Ct)).ShouldBe(6);
        }

        (await LabelsAsync("b00")).ShouldBe([News], ignoreOrder: true);
        (await LabelsAsync("b01")).ShouldBe([DeleteLabel, News], ignoreOrder: true);

        // Re-applying the unchanged policy finds nothing new: no decision, no batch, applied again.
        var again = (await PostAsync<ApprovePolicyResponse>($"/api/policies/{policy}/apply")).JobId.ShouldNotBeNull();
        again.ShouldNotBe(jobId);
        await h.RunNextAsync();
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Decisions.CountAsync(d => d.Source == SuggestionSource.Policy, Ct)).ShouldBe(6);
            (await db.ActionBatches.CountAsync(Ct)).ShouldBe(1);
            (await db.Jobs.SingleAsync(j => j.Id == again, Ct)).Status.ShouldBe(JobStatus.Completed);
        }
    }

    [Fact]
    public async Task Rejected_and_user_edited_suggestions_are_left_alone()
    {
        // News: b01 has a rejected LLM suggestion, b02 this policy's approved suggestion the user edited in review.
        var policy = await SeedPolicyAsync(AnalysisRunHarness.News, isMixed: false, PolicyAction.Archive);
        await SeedSuggestionAsync("b01", SuggestionStatus.Rejected);
        await SeedSuggestionAsync("b02", SuggestionStatus.Approved, s =>
        {
            (s.Source, s.PolicyId, s.Edited, s.Reason) = (SuggestionSource.Policy, policy, true, "Policy: default");
        });

        var jobId = (await PostAsync<ApprovePolicyResponse>($"/api/policies/{policy}/approve")).JobId.ShouldNotBeNull();
        await h.RunNextAsync();
        h.Progress(jobId)[^1].Message.ShouldBe("Matched 4, superseded 0, unmatched 0, skipped 2 (rejected 1, edited 1)");
        await h.RunNextAsync();

        await using (var db = postgres.CreateDbContext())
        {
            var suggestions = await db.Suggestions.AsNoTracking().ToDictionaryAsync(s => s.MessageId, Ct);
            (suggestions["b01"].Source, suggestions["b01"].Status).ShouldBe((SuggestionSource.Llm, SuggestionStatus.Rejected));
            (suggestions["b02"].Status, suggestions["b02"].TopicLabel, suggestions["b02"].Edited)
                .ShouldBe((SuggestionStatus.Applied, Kept, true));
            (await db.Decisions.Where(d => d.Source == SuggestionSource.Policy).Select(d => d.MessageId).OrderBy(m => m).ToListAsync(Ct))
                .ShouldBe(["b00", "b03", "b04", "b05"]);
        }

        (await LabelsAsync("b01")).ShouldContain("INBOX");
        (await LabelsAsync("b02")).ShouldBe([Kept], ignoreOrder: true);
    }

    [Fact]
    public async Task A_policy_with_a_topic_less_outcome_is_not_approved_or_applied()
    {
        var policy = await SeedPolicyAsync(AnalysisRunHarness.Shop, isMixed: true, PolicyAction.Archive,
            ("Deals", "offer 1", PolicyAction.Delete));
        await SeedSuggestionAsync("a00", SuggestionStatus.Pending);
        await using (var db = postgres.CreateDbContext())
        {
            await db.SenderPolicyRules.Where(r => r.PolicyId == policy).ExecuteUpdateAsync(u => u.SetProperty(r => r.TopicLabel, ""), Ct);
        }

        var approve = await h.PostWithoutBodyAsync($"/api/policies/{policy}/approve");
        approve.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await approve.Content.ReadAsStringAsync(Ct)).ShouldContain("Rule 'Deals' has no usable topic label");

        await using (var db = postgres.CreateDbContext())
        {
            (await db.SenderPolicies.SingleAsync(p => p.Id == policy, Ct)).Status.ShouldBe(PolicyStatus.Proposed);
            (await db.Jobs.CountAsync(j => j.Type == PolicyApplyJob.JobType, Ct)).ShouldBe(0);
            await db.SenderPolicies.Where(p => p.Id == policy).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PolicyStatus.Approved), Ct);
            await db.SenderPolicyRules.Where(r => r.PolicyId == policy)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, PolicyStatus.Approved), Ct);
        }

        (await h.PostWithoutBodyAsync($"/api/policies/{policy}/apply")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.SingleAsync(s => s.MessageId == "a00", Ct)).Status.ShouldBe(SuggestionStatus.Pending);
            (await db.SenderPolicies.SingleAsync(p => p.Id == policy, Ct)).AppliedAt.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Apply_needs_an_approved_policy()
    {
        var policy = await SeedPolicyAsync(AnalysisRunHarness.News, isMixed: false, PolicyAction.Archive);
        (await h.PostWithoutBodyAsync($"/api/policies/{policy}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PostWithoutBodyAsync($"/api/policies/{Guid.NewGuid()}/apply")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<Guid> SeedPolicyAsync(
        string sender, bool isMixed, PolicyAction action, params (string Name, string Contains, PolicyAction Action)[] rules)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await using var db = postgres.CreateDbContext();
        db.SenderPolicies.Add(new SenderPolicyRow
        {
            Id = id,
            Scope = PolicyScope.Sender,
            ScopeKey = sender,
            IsMixed = isMixed,
            TopicLabel = sender == AnalysisRunHarness.Shop ? Topic : News,
            Action = action,
            Confidence = 0.8,
            Reason = "Synthetic policy",
            Status = PolicyStatus.Proposed,
            CreatedAt = now,
            Rules = [.. rules.Select((r, i) => new SenderPolicyRuleRow
            {
                Id = Guid.NewGuid(),
                PolicyId = id,
                Position = i,
                Name = r.Name,
                Match = new RuleMatch { SubjectContains = r.Contains },
                TopicLabel = Topic,
                Action = r.Action,
                Status = PolicyStatus.Proposed,
                Source = PolicyRuleSource.Llm,
                Reason = "Synthetic rule",
                CreatedAt = now,
            })],
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>An LLM suggestion with the <see cref="Kept"/> topic unless <paramref name="configure"/> changes it.</summary>
    private async Task SeedSuggestionAsync(string messageId, SuggestionStatus status, Action<SuggestionRow>? configure = null)
    {
        await using var db = postgres.CreateDbContext();
        var message = await db.Messages.SingleAsync(m => m.Id == messageId, Ct);
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var suggestion = new SuggestionRow
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            SenderAddress = message.FromAddress,
            Source = SuggestionSource.Llm,
            TopicLabel = Kept,
            Confidence = 0.6,
            Reason = "Synthetic reason",
            CreatedAt = at,
        };
        configure?.Invoke(suggestion);
        suggestion.SetStatus(status, message, at);
        db.Suggestions.Add(suggestion);
        await db.SaveChangesAsync(Ct);
    }

    private async Task StarAsync(string id)
    {
        string[] labels = ["INBOX", "STARRED"];
        h.Gmail.Inner.SetLabels(id, labels);
        await using var db = postgres.CreateDbContext();
        await db.Messages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, labels), Ct);
    }

    /// <summary>The message's Gmail labels: user labels by name, system labels by id; categories left out.</summary>
    private async Task<List<string>> LabelsAsync(string id)
    {
        var names = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Id, l => l.Name);
        return [.. h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds
            .Where(l => !l.StartsWith("CATEGORY_", StringComparison.Ordinal))
            .Select(l => names.GetValueOrDefault(l, l))];
    }

    private async Task<Guid> BatchAsync(Guid policy)
    {
        await using var db = postgres.CreateDbContext();
        var suggestion = await db.Suggestions.Where(s => s.PolicyId == policy).Select(s => s.Id).FirstAsync(Ct);
        return await db.ActionLog.Where(l => l.SuggestionId == suggestion).Select(l => l.BatchId).SingleAsync(Ct);
    }

    private async Task<T> PostAsync<T>(string path)
        where T : class
    {
        var response = await h.PostWithoutBodyAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }
}
