using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Policies;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Rules.Taxonomy;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The taxonomy proposal (#366) end to end: job, draft plan, apply with labels and proposed policies.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TaxonomyProposeTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Plans = "/api/rules/labels/plans";
    private const string Taxonomy = "/api/rules/labels/taxonomy";
    private const string BankNews = "news@bank.example.com";
    private const string BankAlerts = "alerts@bank.example.com";
    private const string Power = "billing@power.example.com";
    private const string Friend = "friend@people.example.com";
    private const string Trusted = "updates@trusted.example.com";

    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.LabelPlans.ExecuteDeleteAsync(Ct);
            await db.SenderPolicies.ExecuteDeleteAsync(Ct);
            await db.ActionLog.ExecuteDeleteAsync(Ct);
            await db.ActionBatches.ExecuteDeleteAsync(Ct);
        }

        await h.InitializeAsync();
        h.Chat.Respond = (_, _, messages, _) => Task.FromResult(FakeTaxonomyResponder.Answer(messages) ?? "{}");
        await SeedSendersAsync();
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Proposal_becomes_a_draft_plan_and_apply_creates_labels_and_proposed_policies_once()
    {
        var powers = await h.Gmail.Inner.CreateLabelAsync("Powers", Ct);
        await using (var db = postgres.CreateDbContext())
        {
            db.SenderPolicies.Add(new SenderPolicyRow
            {
                Id = Guid.CreateVersion7(),
                Scope = PolicyScope.Sender,
                ScopeKey = BankAlerts,
                TopicLabel = "Synthetic/Kept",
                Action = PolicyAction.Keep,
                Confidence = 0.9,
                Reason = "existing",
                Status = PolicyStatus.Approved,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var response = await h.PostWithoutBodyAsync(Taxonomy);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = (await response.Content.ReadFromJsonAsync<JobDto>(Ct)).ShouldNotBeNull();
        job.Type.ShouldBe(TaxonomyProposeJob.JobType);
        (await h.PostWithoutBodyAsync(Taxonomy)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await h.RunNextAsync();

        var prompt = string.Join('\n', h.Chat.Requests.ShouldHaveSingleItem().Select(m => m.Text));
        prompt.ShouldContain(TaxonomyPrompt.ProfilePrefix + BankNews);
        prompt.ShouldNotContain(Friend);
        prompt.ShouldNotContain(Trusted);
        var plan = await LatestAsync();
        plan.Status.ShouldBe(LabelPlanStatus.Draft);
        plan.Warnings.ShouldContain("Fake answer: one label per sender domain.");
        plan.Items.Count.ShouldBe(2);
        var bank = plan.Items.Single(i => i.LabelName == "Bank");
        bank.Kind.ShouldBe(LabelPlanItemKind.Create);
        bank.SenderKeys.ShouldBe([BankNews, BankAlerts]);
        bank.MessageCount.ShouldBe(5);
        var power = plan.Items.Single(i => i.LabelName == "Power");
        power.Kind.ShouldBe(LabelPlanItemKind.NearDuplicate);
        power.TargetLabelId.ShouldBe(powers.Id);
        power.SenderKeys.ShouldBe([Power]);

        await AcceptAsync(plan.Id, bank.Id);
        await AcceptAsync(plan.Id, power.Id);
        var apply = await h.PostWithoutBodyAsync($"{Plans}/{plan.Id}/apply");
        apply.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        (await h.Gmail.Inner.ListLabelsAsync(Ct)).Count(l => l.Name == "Bank").ShouldBe(1);
        h.Gmail.CreateLabelCalls.ShouldBe(["Bank"]);
        (await LatestAsync()).Items.ShouldAllBe(i => i.Status == LabelPlanItemStatus.Applied);
        var policies = await PoliciesAsync();
        policies.Count.ShouldBe(3);
        policies[BankNews].ShouldSatisfyAllConditions(
            p => p.Status.ShouldBe(PolicyStatus.Proposed),
            p => p.Scope.ShouldBe(PolicyScope.Sender),
            p => p.TopicLabel.ShouldBe("Bank"),
            p => p.Action.ShouldBe(PolicyAction.Archive),
            p => p.Confidence.ShouldBe(TaxonomyPrompt.ProposalConfidence),
            p => p.Reason.ShouldBe(TaxonomyPrompt.ProposalReason),
            p => p.PromptVersion.ShouldBe(TaxonomyPrompt.Version),
            p => p.DisplayName.ShouldBe("Synthetic Bank"),
            p => p.Rules.ShouldBeEmpty());
        policies[Power].TopicLabel.ShouldBe("Powers");
        policies[BankAlerts].ShouldSatisfyAllConditions(p => p.Status.ShouldBe(PolicyStatus.Approved), p => p.TopicLabel.ShouldBe("Synthetic/Kept"));

        // A run that died after its Gmail calls repeats the items: no second label, no second policy.
        await using (var db = postgres.CreateDbContext())
        {
            var row = await db.LabelPlans.SingleAsync(p => p.Id == plan.Id, Ct);
            row.WriteItems([.. row.ReadItems().Select(i => i with { Status = LabelPlanItemStatus.Accepted })]);
            row.Status = LabelPlanStatus.Draft;
            await db.SaveChangesAsync(Ct);
        }

        (await h.PostWithoutBodyAsync($"{Plans}/{plan.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        h.Gmail.CreateLabelCalls.Count.ShouldBe(1);
        (await PoliciesAsync()).Count.ShouldBe(3);
        (await LatestAsync()).Status.ShouldBe(LabelPlanStatus.Applied);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.ActionBatches.CountAsync(b => b.Description == "Created label Bank", Ct)).ShouldBe(1);
        }
    }

    [Fact]
    public async Task An_answer_without_labels_fails_the_job_and_keeps_the_previous_plan()
    {
        h.Chat.Respond = (_, _, _, _) => Task.FromResult("not json");

        (await h.PostWithoutBodyAsync(Taxonomy)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == TaxonomyProposeJob.JobType, Ct);
        job.Status.ShouldBe(JobStatus.Failed);
        job.Error.ShouldNotBeNull().ShouldContain("labels array");
        (await db.LabelPlans.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Without_a_chat_model_the_proposal_is_409()
    {
        await h.SetChatModelAsync(null);

        (await h.PostWithoutBodyAsync(Taxonomy)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Taxonomy_settings_are_bounded()
    {
        (await h.PutAsync("/api/settings", new { taxonomyMaxSenders = 5 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PutAsync("/api/settings", new { taxonomyMaxLabels = 61 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PutAsync("/api/settings", new { taxonomyMaxSenders = 10, taxonomyMaxLabels = 5 })).IsSuccessStatusCode.ShouldBeTrue();

        using var scope = h.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(Ct);
        (saved.TaxonomyMaxSenders, saved.TaxonomyMaxLabels).ShouldBe((10, 5));
    }

    /// <summary>Bulk senders of two areas, a human sender and one on an allowlisted domain; the newest mail first.</summary>
    private async Task SeedSendersAsync()
    {
        (string Address, int Count, SenderKind Kind)[] senders =
        [
            (BankNews, 3, SenderKind.Bulk), (BankAlerts, 2, SenderKind.Bulk), (Power, 2, SenderKind.Bulk),
            (Friend, 9, SenderKind.Human), (Trusted, 9, SenderKind.Bulk),
        ];
        await using var db = postgres.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        foreach (var (address, count, kind) in senders)
        {
            var domain = address[(address.IndexOf('@') + 1)..];
            db.Senders.Add(new SenderRow
            {
                Address = address,
                Domain = domain,
                CanonicalAddress = address,
                CanonicalDomain = domain,
                DisplayName = address.StartsWith("news", StringComparison.Ordinal) ? "Synthetic Bank" : null,
                TotalCount = count,
                Kind = kind,
                UpdatedAt = now,
            });
            for (var i = 0; i < count; i++)
            {
                db.Messages.Add(new MessageRow
                {
                    Id = $"tx-{address}-{i}",
                    ThreadId = $"tx-{address}-{i}",
                    FromAddress = address,
                    CanonicalAddress = address,
                    CanonicalDomain = domain,
                    Subject = $"Synthetic update {i}",
                    InternalDate = now.AddDays(-i),
                    LabelIds = ["INBOX"],
                    Category = MessageCategory.Updates,
                    FetchedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(Ct);
        using var scope = h.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(
            s => s with { Protection = s.Protection with { AllowlistedDomains = ["trusted.example.com"] } }, Ct);
    }

    private async Task AcceptAsync(Guid planId, Guid itemId)
    {
        var client = h.Host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        var response = await client.PatchAsJsonAsync($"{Plans}/{planId}/items/{itemId}", new UpdatePlanItemRequest("accepted", null, null), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<LabelPlanDto> LatestAsync() =>
        (await h.Host.CreateClient().GetFromJsonAsync<LabelPlanDto>($"{Plans}/latest", Ct)).ShouldNotBeNull();

    private async Task<Dictionary<string, SenderPolicyRow>> PoliciesAsync()
    {
        await using var db = postgres.CreateDbContext();
        return await db.SenderPolicies.AsNoTracking().Include(p => p.Rules).ToDictionaryAsync(p => p.ScopeKey, Ct);
    }
}
