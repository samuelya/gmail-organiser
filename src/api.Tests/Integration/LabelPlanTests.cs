using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class LabelPlanTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Plans = "/api/rules/labels/plans";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider time = new(Now);
    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeGmailClient Gmail => host.Services.GetRequiredService<FakeGmailClient>();

    private CountingGmailClient Counting => host.Services.GetRequiredService<CountingGmailClient>();

    public async ValueTask InitializeAsync()
    {
        await postgres.ResetFetchStateAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.LabelPlans.ExecuteDeleteAsync(Ct);
            await db.Filters.ExecuteDeleteAsync(Ct);
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            // One attempt, so an injected rate limit fails the request without real backoff delays.
            var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = 1 }), TimeProvider.System);
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), [], retry));
            services.AddSingleton(sp => new CountingGmailClient(sp.GetRequiredService<FakeGmailClient>()));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<CountingGmailClient>());
            services.AddSingleton<TimeProvider>(time);
        }));
    }

    public async ValueTask DisposeAsync()
    {
        await host.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.LabelPlans.ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task Create_builds_a_draft_from_gmail_counts_and_active_filters()
    {
        var labels = await SeedMailboxAsync();

        var response = await SendAsync(HttpMethod.Post, Plans);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var plan = (await response.Content.ReadFromJsonAsync<LabelPlanDto>(Ct)).ShouldNotBeNull();
        response.Headers.Location!.ToString().ShouldBe($"{Plans}/{plan.Id}");
        plan.Status.ShouldBe(LabelPlanStatus.Draft);
        plan.CreatedAt.ShouldBe(Now);
        plan.Warnings.ShouldBeEmpty();
        plan.LabelCount.ShouldBe(FakeLabelStore.SeedUserLabelNames.Count + 3);
        plan.Items.Count.ShouldBe(3);

        var empty = plan.Items.Single(i => i.Kind == LabelPlanItemKind.Empty);
        empty.LabelId.ShouldBe(labels.Unused.Id);
        empty.AffectedFilterIds.ShouldBe(["filter-active"]);
        var duplicate = plan.Items.Single(i => i.Kind == LabelPlanItemKind.NearDuplicate);
        duplicate.LabelId.ShouldBe(labels.Duplicate.Id);
        duplicate.MessageCount.ShouldBe(1);
        duplicate.TargetLabelId.ShouldBe(labels.Receipts.Id);
        var nest = plan.Items.Single(i => i.Kind == LabelPlanItemKind.Nest);
        nest.LabelId.ShouldBe(labels.Receipts.Id);
        nest.MessageCount.ShouldBe(3);
        nest.ProposedName.ShouldBe("Synthetic/Receipts");
        plan.Items.ShouldAllBe(i => i.Status == LabelPlanItemStatus.Proposed && i.Rationale.Length > 0);
        plan.Items.ShouldNotContain(i => i.LabelId == labels.Protected.Id);

        (await GetAsync<LabelPlanDto>($"{Plans}/latest")).Id.ShouldBe(plan.Id);
        (await GetAsync<LabelPlanDto>($"{Plans}/{plan.Id}")).Items.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_new_plan_discards_the_previous_draft_and_concurrent_creates_leave_one_draft()
    {
        await SeedMailboxAsync();
        var first = await CreateAsync();

        time.SetUtcNow(Now.AddMinutes(1));
        var responses = await Task.WhenAll(SendAsync(HttpMethod.Post, Plans), SendAsync(HttpMethod.Post, Plans));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        (await GetAsync<LabelPlanDto>($"{Plans}/{first.Id}")).Status.ShouldBe(LabelPlanStatus.Discarded);
        await using var db = postgres.CreateDbContext();
        (await db.LabelPlans.CountAsync(p => p.Status == LabelPlanStatus.Draft, Ct)).ShouldBe(1);
        (await db.LabelPlans.CountAsync(Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task Latest_and_an_unknown_plan_are_404_when_there_is_none()
    {
        (await host.CreateClient().GetAsync($"{Plans}/latest", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await host.CreateClient().GetAsync($"{Plans}/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_item_is_accepted_rejected_and_edited_while_the_plan_is_a_draft()
    {
        var labels = await SeedMailboxAsync();
        var plan = await CreateAsync();
        var nest = plan.Items.Single(i => i.Kind == LabelPlanItemKind.Nest);
        var duplicate = plan.Items.Single(i => i.Kind == LabelPlanItemKind.NearDuplicate);

        var edited = await PatchOkAsync(plan.Id, nest.Id, new UpdatePlanItemRequest("accepted", "Synthetic/Receipts Archive", null));
        var item = edited.Items.Single(i => i.Id == nest.Id);
        item.Status.ShouldBe(LabelPlanItemStatus.Accepted);
        item.ProposedName.ShouldBe("Synthetic/Receipts Archive");
        edited.UpdatedAt.ShouldBe(Now);

        var retargeted = await PatchOkAsync(plan.Id, duplicate.Id, new UpdatePlanItemRequest("rejected", null, labels.Nested.Id));
        var merge = retargeted.Items.Single(i => i.Id == duplicate.Id);
        merge.Status.ShouldBe(LabelPlanItemStatus.Rejected);
        merge.TargetLabelId.ShouldBe(labels.Nested.Id);
        merge.TargetLabelName.ShouldBe(labels.Nested.Name);
        retargeted.Items.Single(i => i.Id == nest.Id).Status.ShouldBe(LabelPlanItemStatus.Accepted);

        (await GetAsync<LabelPlanDto>($"{Plans}/latest")).Items.Single(i => i.Id == duplicate.Id).Status.ShouldBe(LabelPlanItemStatus.Rejected);
    }

    [Fact]
    public async Task Invalid_item_edits_are_400_and_unknown_ones_404()
    {
        var labels = await SeedMailboxAsync();
        var plan = await CreateAsync();
        var nest = plan.Items.Single(i => i.Kind == LabelPlanItemKind.Nest);
        var duplicate = plan.Items.Single(i => i.Kind == LabelPlanItemKind.NearDuplicate);

        (await PatchAsync(plan.Id, nest.Id, new UpdatePlanItemRequest("applied", null, null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, nest.Id, new UpdatePlanItemRequest(null, "Synthetic//Receipts", null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, nest.Id, new UpdatePlanItemRequest(null, "INBOX", null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, duplicate.Id, new UpdatePlanItemRequest(null, "Synthetic/Other", null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, duplicate.Id, new UpdatePlanItemRequest(null, null, "Label_unknown"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, duplicate.Id, new UpdatePlanItemRequest(null, null, duplicate.LabelId))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, duplicate.Id, new UpdatePlanItemRequest(null, null, labels.Protected.Id))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, nest.Id, new UpdatePlanItemRequest(null, null, labels.Unused.Id))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, duplicate.Id, new UpdatePlanItemRequest(null, null, labels.Unused.Id))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, nest.Id, new UpdatePlanItemRequest(null, labels.Nested.Name, null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, nest.Id, new UpdatePlanItemRequest(null, labels.Protected.Name.ToUpperInvariant(), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, nest.Id, new UpdatePlanItemRequest(null, $"{labels.Duplicate.Name}/Child", null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(plan.Id, Guid.NewGuid(), new UpdatePlanItemRequest("accepted", null, null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PatchAsync(Guid.NewGuid(), nest.Id, new UpdatePlanItemRequest("accepted", null, null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await GetAsync<LabelPlanDto>($"{Plans}/{plan.Id}")).Items.ShouldAllBe(i => i.Status == LabelPlanItemStatus.Proposed);
    }

    [Fact]
    public async Task Discard_hides_the_plan_from_latest_and_freezes_its_items()
    {
        await SeedMailboxAsync();
        var plan = await CreateAsync();
        time.SetUtcNow(Now.AddMinutes(2));

        var response = await SendAsync(HttpMethod.Post, $"{Plans}/{plan.Id}/discard");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var discarded = (await response.Content.ReadFromJsonAsync<LabelPlanDto>(Ct)).ShouldNotBeNull();
        discarded.Status.ShouldBe(LabelPlanStatus.Discarded);
        discarded.UpdatedAt.ShouldBe(Now.AddMinutes(2));
        (await host.CreateClient().GetAsync($"{Plans}/latest", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"{Plans}/{plan.Id}/discard")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PatchAsync(plan.Id, plan.Items[0].Id, new UpdatePlanItemRequest("accepted", null, null))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(LabelPlanStatus.Applying)]
    [InlineData(LabelPlanStatus.Applied)]
    public async Task A_plan_being_or_already_applied_cannot_be_discarded_or_edited(LabelPlanStatus status)
    {
        await SeedMailboxAsync();
        var plan = await CreateAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.LabelPlans.Where(p => p.Id == plan.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status), Ct);
        }

        (await SendAsync(HttpMethod.Post, $"{Plans}/{plan.Id}/discard")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PatchAsync(plan.Id, plan.Items[0].Id, new UpdatePlanItemRequest("accepted", null, null))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await GetAsync<LabelPlanDto>($"{Plans}/{plan.Id}")).Status.ShouldBe(status);
    }

    [Fact]
    public async Task A_label_deleted_in_gmail_while_counting_is_left_out()
    {
        var labels = await SeedMailboxAsync();
        Counting.BeforeLabelTotals = _ =>
        {
            Gmail.DeleteLabel(labels.Unused.Id);
            return Task.CompletedTask;
        };

        var plan = await CreateAsync();

        plan.LabelCount.ShouldBe(FakeLabelStore.SeedUserLabelNames.Count + 2);
        plan.Items.ShouldNotContain(i => i.LabelId == labels.Unused.Id);
        plan.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Another_gmail_error_while_counting_is_502_and_stores_no_plan()
    {
        await SeedMailboxAsync();
        Gmail.FailNext(HttpStatusCode.BadRequest, 1, afterCalls: 2);

        var response = await SendAsync(HttpMethod.Post, Plans);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        await using var db = postgres.CreateDbContext();
        (await db.LabelPlans.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Create_is_503_when_gmail_is_not_connected()
    {
        await host.Services.GetRequiredService<FakeTokenStore>().DeleteAsync(Ct);

        var response = await SendAsync(HttpMethod.Post, Plans);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Gmail not connected");
    }

    [Fact]
    public async Task A_rate_limit_while_counting_is_503_and_stores_no_plan()
    {
        await SeedMailboxAsync();
        var previous = await CreateAsync();
        Gmail.FailNext(HttpStatusCode.TooManyRequests, 1, afterCalls: 2);

        var response = await SendAsync(HttpMethod.Post, Plans);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("rate-limiting");
        await using var db = postgres.CreateDbContext();
        (await db.LabelPlans.SingleAsync(Ct)).Id.ShouldBe(previous.Id);
        (await GetAsync<LabelPlanDto>($"{Plans}/latest")).Status.ShouldBe(LabelPlanStatus.Draft);
    }

    /// <summary>
    /// Adds to the fake's seed labels: a near-duplicate of the receipts label, the protected delete label and an unused
    /// label one active filter (and one deleted filter) references.
    /// </summary>
    private async Task<SeededLabels> SeedMailboxAsync()
    {
        var app = await host.Services.GetRequiredService<ISettingsStore>().GetAsync(Ct);
        var all = await Gmail.ListLabelsAsync(Ct);
        var receipts = all.Single(l => l.Name == FakeLabelStore.SeedUserLabelNames[2]);
        var nested = all.Single(l => l.Name == FakeLabelStore.SeedUserLabelNames[1]);
        var duplicate = await Gmail.CreateLabelAsync("Synthetic Receipt", Ct);
        var protectedLabel = await Gmail.CreateLabelAsync(app.DeleteLabelName, Ct);
        var unused = await Gmail.CreateLabelAsync("Unused Topic", Ct);
        for (var i = 0; i < 3; i++)
        {
            Gmail.AddMessage(Message($"r{i}", receipts.Id));
        }

        Gmail.AddMessage(Message("d0", duplicate.Id));
        Gmail.AddMessage(Message("n0", nested.Id));

        await using var db = postgres.CreateDbContext();
        db.Filters.AddRange(Filter("filter-active", unused.Id, deletedAt: null), Filter("filter-deleted", unused.Id, deletedAt: Now));
        await db.SaveChangesAsync(Ct);
        return new SeededLabels(receipts, duplicate, protectedLabel, unused, nested);
    }

    private static FakeMessage Message(string id, string labelId) =>
        new(id, id, "sender@example.com", "Synthetic subject", Now.AddDays(-1), ["INBOX", labelId]);

    private static FilterRow Filter(string id, string labelId, DateTimeOffset? deletedAt) => new()
    {
        Id = id,
        Criteria = FilterRow.WriteCriteria(new GmailFilterCriteria(From: "list@example.com")),
        Action = FilterRow.WriteAction(new GmailFilterAction([labelId], ["INBOX"])),
        CriteriaSummary = "from:list@example.com",
        FirstSeenAt = Now,
        LastSeenAt = Now,
        DeletedAt = deletedAt,
        UpdatedAt = Now,
    };

    private async Task<LabelPlanDto> CreateAsync()
    {
        var response = await SendAsync(HttpMethod.Post, Plans);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<LabelPlanDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<T> GetAsync<T>(string path)
        where T : class => (await host.CreateClient().GetFromJsonAsync<T>(path, Ct)).ShouldNotBeNull();

    private async Task<LabelPlanDto> PatchOkAsync(Guid planId, Guid itemId, UpdatePlanItemRequest request)
    {
        var response = await PatchAsync(planId, itemId, request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<LabelPlanDto>(Ct)).ShouldNotBeNull();
    }

    private Task<HttpResponseMessage> PatchAsync(Guid planId, Guid itemId, UpdatePlanItemRequest request) =>
        SendAsync(HttpMethod.Patch, $"{Plans}/{planId}/items/{itemId}", JsonContent.Create(request));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.SendAsync(new HttpRequestMessage(method, path) { Content = content }, Ct);
    }

    private sealed record SeededLabels(GmailLabel Receipts, GmailLabel Duplicate, GmailLabel Protected, GmailLabel Unused, GmailLabel Nested);
}
