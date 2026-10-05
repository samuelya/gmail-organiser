using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Policies;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The top senders scope (#357): one policy proposal per sender, resumable by sender.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TopSendersRunTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Mixed = "mixed@example.com";
    private const string Plain = "plain@example.com";
    private const string Lister = "lister@example.com";
    private const string Taken = "taken@example.com";
    private const string Tiny = "tiny@example.com";
    private const string Last = "last@example.com";
    private const string ListId = "<news.example.com>";
    private static readonly DateTimeOffset Newest = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        h.Chat.Respond = (_, _, messages, _) => Task.FromResult(FakeAnalysisResponder.Answer(messages));
        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
        await db.Senders.ExecuteDeleteAsync(Ct);

        // Most mail first: mixed 8 (two categories), plain 6, lister 5 (all one list), taken 7 (has a policy),
        // last 4, tiny 2 (below the minimum group size of 3).
        Seed(db, Mixed, 8, i => i % 2 == 0 ? MessageCategory.Updates : MessageCategory.Promotions);
        Seed(db, Taken, 7);
        Seed(db, Plain, 6);
        Seed(db, Lister, 5, listId: " <News.Example.com> ");
        Seed(db, Last, 4);
        Seed(db, Tiny, 2);
        db.SenderPolicies.Add(new SenderPolicyRow
        {
            Id = Guid.NewGuid(),
            Scope = PolicyScope.Sender,
            ScopeKey = Taken,
            TopicLabel = "Updates/Taken",
            Action = PolicyAction.Archive,
            Confidence = 0.9,
            Reason = "Synthetic",
            Status = PolicyStatus.Proposed,
            CreatedAt = Newest,
        });
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Preview_lists_the_senders_without_model_calls()
    {
        var preview = await PreviewAsync(new { scope = "top_senders", count = 10 });

        preview.Senders.ShouldNotBeNull().Select(s => (s.Scope, s.ScopeKey, s.Count)).ShouldBe([
            ("sender", Mixed, 8), ("sender", Plain, 6), ("list", ListId, 5), ("sender", Last, 4)]);
        preview.Senders[0].DisplayName.ShouldBe("Display mixed");
        (preview.Messages, preview.EstimatedLlmCalls, preview.EstimatedDerived).ShouldBe((23, 4, 0));
        h.Chat.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_run_of_three_proposes_three_policies_and_resumes_with_the_next_sender_after_a_restart()
    {
        using var stop = new CancellationTokenSource();
        var answer = h.Chat.Respond;
        h.Chat.Respond = (ids, call, messages, ct) =>
        {
            if (call == 2)
            {
                // The host stops while the second sender waits for the model: its answer is lost.
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return answer(ids, call, messages, ct);
        };
        var run = await h.StartAsync(new StartAnalysisRunRequest("top_senders", null, null, 3, null));
        run.RequestedCount.ShouldBe(3);

        await h.RunNextAsync(stop.Token);
        (await h.GetRunAsync(run.Id)).PoliciesProposed.ShouldBe(1);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.Scope, done.PoliciesProposed, done.MessagesCovered, done.LlmCalls, done.MixedGroups, done.FailedMessages)
            .ShouldBe(("completed", "top_senders", 3, 19, 3, 1, 0));
        done.PromptVersion.ShouldBe("sender-policy-v1");
        h.Chat.Calls.ShouldBe(4);

        await using var db = postgres.CreateDbContext();
        var policies = await db.SenderPolicies.AsNoTracking().Include(p => p.Rules).Where(p => p.RunId == run.Id).ToListAsync(Ct);
        policies.Select(p => (p.Scope, p.ScopeKey)).OrderBy(p => p.ScopeKey).ShouldBe([
            (PolicyScope.List, ListId), (PolicyScope.Sender, Mixed), (PolicyScope.Sender, Plain)]);
        policies.ShouldAllBe(p => p.Status == PolicyStatus.Proposed && p.Model == AnalysisRunHarness.ChatModel);
        var mixed = policies.Single(p => p.IsMixed);
        mixed.ScopeKey.ShouldBe(Mixed);
        mixed.Rules.OrderBy(r => r.Position).Select(r => r.Position).ShouldBe([0, 1]);
        mixed.Rules.ShouldAllBe(r => r.Source == PolicyRuleSource.Llm && r.Status == PolicyStatus.Proposed);
        (await db.SenderPolicies.CountAsync(p => p.ScopeKey == Taken, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Invalid_output_counts_the_sender_as_failed_and_the_run_continues()
    {
        var answer = h.Chat.Respond;
        h.Chat.Respond = (ids, call, messages, ct) =>
            call == 1 ? Task.FromResult("Sorry, I cannot help with that.") : answer(ids, call, messages, ct);
        var run = await h.StartAsync(new StartAnalysisRunRequest("top_senders", null, null, 2, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.PoliciesProposed, done.FailedMessages, done.LlmCalls, done.MessagesCovered, done.Error)
            .ShouldBe(("completed", 1, 1, 2, 6, (string?)null));
        h.Chat.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_sender_address_targets_one_sender_whose_open_policy_blocks_a_new_one_and_a_rejected_one_is_replaced()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("top_senders", " Tiny@Example.com ", null, 5, null));
        run.SenderAddress.ShouldBe(Tiny);
        await h.RunNextAsync();
        (await h.GetRunAsync(run.Id)).PoliciesProposed.ShouldBe(1);

        var again = await h.StartAsync(new StartAnalysisRunRequest("top_senders", Tiny, null, 5, null));
        await h.RunNextAsync();
        (await h.GetRunAsync(again.Id)).PoliciesProposed.ShouldBe(0);
        h.Chat.Calls.ShouldBe(1);

        // A rejected policy does not hold the sender back: the new proposal replaces it.
        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.Where(p => p.ScopeKey == Tiny).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PolicyStatus.Rejected), Ct);
        var third = await h.StartAsync(new StartAnalysisRunRequest("top_senders", Tiny, null, 5, null));
        await h.RunNextAsync();
        (await h.GetRunAsync(third.Id)).PoliciesProposed.ShouldBe(1);
        (await db.SenderPolicies.AsNoTracking().SingleAsync(p => p.ScopeKey == Tiny, Ct)).RunId.ShouldBe(third.Id);
    }

    [Theory]
    [InlineData(101, null, "count")]
    [InlineData(0, null, "count")]
    [InlineData(3, "unknown@example.com", "senderAddress")]
    public async Task Invalid_selection_is_a_400(int count, string? sender, string field)
    {
        var response = await h.PostAsync("/api/analysis/runs", new StartAnalysisRunRequest("top_senders", sender, null, count, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(field);
    }

    private async Task<GroupingPreviewDto> PreviewAsync(object request)
    {
        var response = await h.PostAsync("/api/analysis/preview", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GroupingPreviewDto>(Ct)).ShouldNotBeNull();
    }

    private static void Seed(
        Data.AppDbContext db, string address, int count, Func<int, MessageCategory>? category = null, string? listId = null)
    {
        var name = address.Split('@')[0];
        for (var i = 0; i < count; i++)
        {
            db.Messages.Add(new MessageRow
            {
                Id = $"{name}-{i:D2}",
                ThreadId = $"{name}-{i:D2}",
                FromAddress = address,
                CanonicalAddress = address,
                CanonicalDomain = "example.com",
                FromName = $"Display {name}",
                Subject = $"Synthetic update {i}",
                InternalDate = Newest.AddHours(-i),
                LabelIds = ["INBOX"],
                Category = category?.Invoke(i) ?? MessageCategory.Updates,
                ListId = listId,
                FetchedAt = Newest,
                UpdatedAt = Newest,
            });
        }

        db.Senders.Add(new SenderRow
        {
            Address = address,
            Domain = "example.com",
            CanonicalAddress = address,
            CanonicalDomain = "example.com",
            DisplayName = $"Display {name}",
            TotalCount = count,
            UpdatedAt = Newest,
        });
    }
}
