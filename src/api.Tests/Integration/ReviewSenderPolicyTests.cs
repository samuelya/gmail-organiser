using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The review sender carries the approved policy covering it (#425): a sender policy on the relay-decoded address, then a
/// List-Id's, then a domain's; proposed policies never count.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewSenderPolicyTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Decoded = "person@example.net";
    private const string NewsList = "news.list.example.com";
    private static readonly DateTimeOffset Created = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        // The shop mails through a relay: its canonical sender and domain are the decoded ones.
        await db.Messages.Where(m => m.FromAddress == AnalysisRunHarness.Shop).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.CanonicalAddress, Decoded).SetProperty(m => m.CanonicalDomain, "example.net"), Ct);
        await db.Messages.Where(m => m.FromAddress == AnalysisRunHarness.News).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.CanonicalAddress, AnalysisRunHarness.News).SetProperty(m => m.CanonicalDomain, "example.com")
            .SetProperty(m => m.ListId, $"<{NewsList.ToUpperInvariant()}>"), Ct);
        await db.Messages.Where(m => m.FromAddress == AnalysisRunHarness.Billing).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.CanonicalAddress, AnalysisRunHarness.Billing).SetProperty(m => m.CanonicalDomain, "example.com"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        }

        await h.DisposeAsync();
    }

    [Fact]
    public async Task Sender_carries_the_approved_policy_by_sender_then_list_then_domain()
    {
        var relay = Policy(PolicyScope.Sender, Decoded, PolicyStatus.Approved);
        var relayDomain = Policy(PolicyScope.Domain, "example.net", PolicyStatus.Approved);
        var list = Policy(PolicyScope.List, $"<{NewsList}>", PolicyStatus.Approved);
        var domain = Policy(PolicyScope.Domain, "example.com", PolicyStatus.Approved);
        var proposed = Policy(PolicyScope.Sender, AnalysisRunHarness.Billing, PolicyStatus.Proposed);
        await using (var db = postgres.CreateDbContext())
        {
            db.SenderPolicies.AddRange(relay, relayDomain, list, domain, proposed);
            await db.SaveChangesAsync(Ct);
        }

        (await DetailAsync(AnalysisRunHarness.Shop)).Sender.PolicyId.ShouldBe(relay.Id);
        (await DetailAsync(AnalysisRunHarness.News)).Sender.PolicyId.ShouldBe(list.Id);
        (await DetailAsync(AnalysisRunHarness.Billing)).Sender.PolicyId.ShouldBe(domain.Id);

        var senders = await (await h.GetAsync("/api/review/senders")).Content.ReadFromJsonAsync<PagedDto<ReviewSenderDto>>(Ct);
        senders.ShouldNotBeNull().Items.ToDictionary(s => s.Address, s => s.PolicyId).ShouldBe(new Dictionary<string, Guid?>
        {
            [AnalysisRunHarness.Shop] = relay.Id,
            [AnalysisRunHarness.News] = list.Id,
            [AnalysisRunHarness.Billing] = domain.Id,
        }, ignoreOrder: true);
    }

    [Fact]
    public async Task Sender_without_an_approved_policy_has_none()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.SenderPolicies.Add(Policy(PolicyScope.Sender, Decoded, PolicyStatus.Proposed));
            // Policies are keyed by the canonical address, so one on the raw relay address does not cover it.
            db.SenderPolicies.Add(Policy(PolicyScope.Sender, AnalysisRunHarness.Shop, PolicyStatus.Approved));
            await db.SaveChangesAsync(Ct);
        }

        (await DetailAsync(AnalysisRunHarness.Shop)).Sender.PolicyId.ShouldBeNull();
    }

    private async Task<ReviewSenderDetailDto> DetailAsync(string address) =>
        (await (await h.GetAsync($"/api/review/senders/{address}")).Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull();

    private static SenderPolicyRow Policy(PolicyScope scope, string key, PolicyStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Scope = scope,
        ScopeKey = key,
        TopicLabel = "Example/Topic",
        Action = PolicyAction.Archive,
        Confidence = 0.9,
        Reason = "synthetic",
        Status = status,
        CreatedAt = Created,
    };
}
