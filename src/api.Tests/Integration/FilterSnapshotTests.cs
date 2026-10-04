using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Rules;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class FilterSnapshotTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider time = new(Now);
    private WebApplicationFactory<Program> host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeGmailClient Gmail => host.Services.GetRequiredService<FakeGmailClient>();

    public async ValueTask InitializeAsync()
    {
        await postgres.ResetFetchStateAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Filters.ExecuteDeleteAsync(Ct);
            await db.FetchState.ExecuteUpdateAsync(s => s.SetProperty(r => r.FiltersSyncedAt, (DateTimeOffset?)null), Ct);
        }

        host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(services =>
        {
            // One attempt, so an injected rate limit fails the request without real backoff delays.
            var retry = new GmailRetryPolicy(Options.Create(new GmailOptions { MaxRetryAttempts = 1 }), TimeProvider.System);
            services.AddSingleton(sp => new FakeGmailClient(sp.GetRequiredService<FakeTokenStore>(), [], retry));
            services.AddScoped<IGmailClient>(sp => sp.GetRequiredService<FakeGmailClient>());
            services.AddSingleton<TimeProvider>(time);
        }));
    }

    public async ValueTask DisposeAsync() => await host.DisposeAsync();

    [Fact]
    public async Task Before_the_first_sync_the_list_is_empty_with_no_sync_time()
    {
        var list = await ListAsync();

        list.SyncedAt.ShouldBeNull();
        list.ActiveCount.ShouldBe(0);
        list.Limit.ShouldBe(1000);
        list.Filters.ShouldBeEmpty();
    }

    [Fact]
    public async Task First_sync_adds_every_filter_with_label_names_resolved()
    {
        var result = await SyncAsync();

        result.ShouldBe(new FilterSyncResultDto(3, 3, 0, Now));
        var list = await ListAsync();
        list.SyncedAt.ShouldBe(Now);
        list.ActiveCount.ShouldBe(3);
        list.Filters.Select(f => f.Id).ShouldBe(["fake-filter-1", "fake-filter-2", "fake-filter-3"]);
        list.Filters.Select(f => f.CriteriaSummary).ShouldBe(
            ["from:news@example.com", "subject:\"Synthetic receipt\" has:attachment", "to:lists@example.com"]);

        var news = list.Filters[0];
        news.Action.AddLabels.ShouldBe([new LabelRefDto("Label_1", FakeLabelStore.SeedUserLabelNames[0])]);
        news.Action.SkipInbox.ShouldBeTrue();
        news.Action.MarkRead.ShouldBeFalse();
        news.FirstSeenAt.ShouldBe(Now);
        news.CreatedByApp.ShouldBeFalse();
        list.Filters[1].Action.MarkRead.ShouldBeTrue();
        list.Filters[1].Criteria.HasAttachment.ShouldBe(true);
        list.Filters[2].Action.AddLabels.ShouldBe([new LabelRefDto(FakeFilterStore.MissingLabelId, null)]);
    }

    [Fact]
    public async Task A_filter_gone_from_gmail_is_marked_deleted_once_and_undeleted_when_it_reappears()
    {
        await SyncAsync();
        var removedFilter = FakeFilterStore.Seed[0];
        await Gmail.DeleteFilterAsync(removedFilter.Id, Ct);
        var deletedAt = Now.AddMinutes(5);
        time.SetUtcNow(deletedAt);

        (await SyncAsync()).ShouldBe(new FilterSyncResultDto(2, 0, 1, deletedAt));
        var active = await ListAsync();
        active.ActiveCount.ShouldBe(2);
        active.Filters.ShouldNotContain(f => f.Id == removedFilter.Id);
        var all = await ListAsync(includeDeleted: true);
        var deleted = all.Filters.Single(f => f.Id == removedFilter.Id);
        deleted.DeletedAt.ShouldBe(deletedAt);
        deleted.DeletedByApp.ShouldBeFalse();
        all.ActiveCount.ShouldBe(2);

        // Syncing again is idempotent: the deletion keeps its first time.
        time.SetUtcNow(Now.AddMinutes(10));
        (await SyncAsync()).ShouldBe(new FilterSyncResultDto(2, 0, 0, Now.AddMinutes(10)));
        (await ListAsync(includeDeleted: true)).Filters.Single(f => f.Id == removedFilter.Id).DeletedAt.ShouldBe(deletedAt);

        Gmail.RestoreFilter(removedFilter);
        time.SetUtcNow(Now.AddMinutes(15));
        (await SyncAsync()).ShouldBe(new FilterSyncResultDto(3, 1, 0, Now.AddMinutes(15)));
        var restored = (await ListAsync()).Filters.Single(f => f.Id == removedFilter.Id);
        restored.DeletedAt.ShouldBeNull();
        restored.FirstSeenAt.ShouldBe(Now);
    }

    [Fact]
    public async Task A_changed_filter_is_rewritten_and_concurrent_syncs_converge()
    {
        await SyncAsync();
        var changed = FakeFilterStore.Seed[2] with { Criteria = new GmailFilterCriteria(To: "lists@example.com", Size: 1000, SizeComparison: GmailSizeComparison.Larger) };
        Gmail.RestoreFilter(changed);

        var results = await Task.WhenAll(SyncAsync(), SyncAsync());

        results.ShouldAllBe(r => r.Total == 3 && r.Added == 0 && r.Removed == 0);
        var filter = (await ListAsync()).Filters.Single(f => f.Id == changed.Id);
        filter.CriteriaSummary.ShouldBe("to:lists@example.com larger:1000");
        filter.Criteria.SizeComparison.ShouldBe("larger");
        await using var db = postgres.CreateDbContext();
        (await db.Filters.CountAsync(Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task Sync_is_503_when_gmail_is_not_connected()
    {
        await host.Services.GetRequiredService<FakeTokenStore>().DeleteAsync(Ct);

        var response = await PostAsync("/api/rules/filters/sync");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Gmail not connected");
        (await ListAsync()).SyncedAt.ShouldBeNull();
    }

    [Fact]
    public async Task The_snapshot_is_served_without_label_names_while_gmail_is_disconnected()
    {
        await SyncAsync();
        await host.Services.GetRequiredService<FakeTokenStore>().DeleteAsync(Ct);

        var list = await ListAsync();

        list.ActiveCount.ShouldBe(3);
        list.Filters.SelectMany(f => f.Action.AddLabels).ShouldAllBe(l => l.Name == null);
    }

    [Fact]
    public async Task Sync_is_503_when_gmail_keeps_rate_limiting()
    {
        Gmail.FailNext(HttpStatusCode.TooManyRequests, 1);

        var response = await PostAsync("/api/rules/filters/sync");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("rate-limiting");
    }

    [Fact]
    public async Task Sync_claims_the_local_account()
    {
        await SyncAsync();

        await using var db = postgres.CreateDbContext();
        (await db.FetchState.SingleAsync(Ct)).AccountEmail.ShouldBe(FakeGmailClient.AccountEmail);
    }

    private async Task<FilterSyncResultDto> SyncAsync()
    {
        var response = await PostAsync("/api/rules/filters/sync");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<FilterSyncResultDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<FilterListDto> ListAsync(bool includeDeleted = false) =>
        (await host.CreateClient().GetFromJsonAsync<FilterListDto>($"/api/rules/filters?includeDeleted={includeDeleted}", Ct)).ShouldNotBeNull();

    private Task<HttpResponseMessage> PostAsync(string path)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PostAsync(path, null, Ct);
    }
}
