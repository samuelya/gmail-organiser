using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Policy coverage (#360) over the harness mailbox: fetched mail of an approved policy is suggested and applied
/// without the LLM, and analysis runs skip covered mail.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PolicyCoverageTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string News = "Example/News";
    private const string Shop = "Example/Shop";
    private const string AnalysisPromptBuilderHeading = Analysis.Prompts.AnalysisPromptBuilder.PolicyHeading;

    private readonly AnalysisRunHarness h = new(factory, postgres);

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
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Incremental_fetch_suggests_and_applies_new_mail_of_a_covered_sender()
    {
        var policy = await SeedPolicyAsync(AnalysisRunHarness.News, isMixed: false, PolicyAction.Archive);
        await FetchMailboxAsync();
        await using (var db = postgres.CreateDbContext())
        {
            // Known mail is never covered by a fetch; the policy's own apply handles it.
            (await db.Suggestions.CountAsync(Ct)).ShouldBe(0);
        }

        h.Gmail.Inner.AddMessage(NewMessage("n1", AnalysisRunHarness.News));
        h.Gmail.Inner.AddMessage(NewMessage("n2", AnalysisRunHarness.News));
        h.Gmail.Inner.AddMessage(NewMessage("n3", "unknown@example.com"));
        await EnqueueAsync(IncrementalFetchJob.JobType);
        await h.RunNextAsync();

        await using (var db = postgres.CreateDbContext())
        {
            var suggestions = await db.Suggestions.AsNoTracking().OrderBy(s => s.MessageId).ToListAsync(Ct);
            suggestions.Select(s => s.MessageId).ShouldBe(["n1", "n2"]);
            suggestions.ShouldAllBe(s => s.Source == SuggestionSource.Policy && s.Status == SuggestionStatus.Approved
                && s.PolicyId == policy && s.TopicLabel == News && !s.KeepInInbox);
            var apply = await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == ApplyActionsJob.JobType, Ct);
            apply.Status.ShouldBe(JobStatus.Queued);
            // The fetch wrote nothing to Gmail.
            h.Gmail.Inner.Messages.Single(m => m.Id == "n1").LabelIds.ShouldContain("INBOX");
        }

        await h.RunNextAsync();

        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Applied, Ct)).ShouldBe(2);
            (await db.ActionLog.CountAsync(Ct)).ShouldBe(2);
        }

        h.Gmail.Inner.Messages.Single(m => m.Id == "n1").LabelIds.ShouldNotContain("INBOX");
        h.Gmail.Inner.Messages.Single(m => m.Id == "n3").LabelIds.ShouldContain("INBOX");
    }

    [Fact]
    public async Task Setting_off_leaves_fetched_mail_alone()
    {
        await SeedPolicyAsync(AnalysisRunHarness.News, isMixed: false, PolicyAction.Archive);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
                .UpdateAsync(s => s with { PolicyAutoApplyFetched = false }, Ct);
        }

        await FetchMailboxAsync();
        h.Gmail.Inner.AddMessage(NewMessage("n1", AnalysisRunHarness.News));
        await EnqueueAsync(IncrementalFetchJob.JobType);
        await h.RunNextAsync();

        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(m => m.Id == "n1", Ct)).ShouldBe(1);
        (await db.Suggestions.CountAsync(Ct)).ShouldBe(0);
        (await db.Jobs.CountAsync(j => j.Type == ApplyActionsJob.JobType, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Inbox_run_skips_covered_mail_counts_mixed_matches_and_hints_the_policy()
    {
        // News is single-label: left out in SQL. Shop is mixed: "offer 1" matches a00 and a09, the rest go to the model.
        await SeedPolicyAsync(AnalysisRunHarness.News, isMixed: false, PolicyAction.Archive);
        await SeedPolicyAsync(AnalysisRunHarness.Shop, isMixed: true, PolicyAction.Keep, ("Deals", "offer 1"));

        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 100, null));
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        (done.MessagesCovered, done.SkippedMessages).ShouldBe((4 + 5 + 8, 2));
        h.Progress(done.JobId.ShouldNotBeNull())[^1].Message.ShouldEndWith(", 2 covered by policy");
        await using (var db = postgres.CreateDbContext())
        {
            var analysed = await db.Suggestions.AsNoTracking().Select(s => s.MessageId).ToListAsync(Ct);
            analysed.ShouldNotContain(id => id.StartsWith('b'));
            analysed.ShouldNotContain("a00");
            analysed.ShouldNotContain("a09");
        }

        var shopPrompt = h.Chat.Requests.Select(r => string.Join('\n', r.Select(m => m.Text)))
            .Single(t => t.Contains("Weekly offer", StringComparison.Ordinal));
        shopPrompt.ShouldContain(AnalysisPromptBuilderHeading);
        shopPrompt.ShouldContain($"- sender: {AnalysisRunHarness.Shop} | topicLabel: (mixed: decide per email) | mailType: - | action: keep");
        h.Chat.Requests.Select(r => string.Join('\n', r.Select(m => m.Text)))
            .Single(t => t.Contains("Invoice", StringComparison.Ordinal)).ShouldNotContain(AnalysisPromptBuilderHeading);
    }

    private async Task<Guid> SeedPolicyAsync(
        string sender, bool isMixed, PolicyAction action, params (string Name, string Contains)[] rules)
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
            TopicLabel = isMixed ? null : News,
            Action = action,
            Confidence = 0.8,
            Reason = "Synthetic policy",
            Status = PolicyStatus.Approved,
            CreatedAt = now,
            DecidedAt = now,
            Rules = [.. rules.Select((r, i) => new SenderPolicyRuleRow
            {
                Id = Guid.NewGuid(),
                PolicyId = id,
                Position = i,
                Name = r.Name,
                Match = new RuleMatch { SubjectContains = r.Contains },
                TopicLabel = Shop,
                Action = PolicyAction.Archive,
                Status = PolicyStatus.Approved,
                Source = PolicyRuleSource.Llm,
                Reason = "Synthetic rule",
                CreatedAt = now,
            })],
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task FetchMailboxAsync()
    {
        await EnqueueAsync(MailboxFetchJob.JobType);
        await h.RunNextAsync();
    }

    private async Task EnqueueAsync(string type)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobService>().EnqueueAsync(type, JobQueues.Fetch, null, Ct);
    }

    private static FakeMessage NewMessage(string id, string from) =>
        new(id, $"t-{id}", from, $"Newsletter extra {id}", DateTimeOffset.UtcNow, ["INBOX", "CATEGORY_UPDATES"]);
}
