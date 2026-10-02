using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Review;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class LabelsEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Labels_are_cached_until_refreshed()
    {
        var first = await ListAsync();
        first.ShouldContain(new LabelDto("INBOX", "INBOX", "system"));
        first.ShouldContain(l => l.Name == FakeLabelStore.SeedUserLabelNames[0] && l.Type == "user");

        await h.Gmail.Inner.CreateLabelAsync("Synthetic/New", Ct);
        (await ListAsync()).ShouldNotContain(l => l.Name == "Synthetic/New");

        var refreshed = await h.PostAsync("/api/labels/refresh", new { });
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await refreshed.Content.ReadFromJsonAsync<List<LabelDto>>(Ct)).ShouldNotBeNull().ShouldContain(l => l.Name == "Synthetic/New");
        (await ListAsync()).ShouldContain(l => l.Name == "Synthetic/New");

        var catalog = h.Services.GetRequiredService<LabelCatalog>();
        (await catalog.FindByNameAsync("synthetic/new", Ct)).ShouldNotBeNull().Name.ShouldBe("Synthetic/New");
        (await catalog.FindByNameAsync("Missing", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Labels_are_503_when_gmail_is_not_connected()
    {
        await h.Services.GetRequiredService<FakeTokenStore>().DeleteAsync(Ct);

        var response = await h.GetAsync("/api/labels");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Gmail not connected");
    }

    [Fact]
    public async Task A_load_in_flight_during_an_invalidation_does_not_cache_its_stale_list()
    {
        var catalog = h.Services.GetRequiredService<LabelCatalog>();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Gmail.AfterListLabels = async () =>
        {
            h.Gmail.AfterListLabels = null;
            entered.SetResult();
            await release.Task;
        };

        var stale = catalog.GetAsync(Ct);
        await entered.Task.WaitAsync(Ct);
        await h.Gmail.Inner.CreateLabelAsync("Synthetic/Raced", Ct);
        catalog.Invalidate();
        release.SetResult();

        (await stale).ShouldNotContain(l => l.Name == "Synthetic/Raced");
        (await ListAsync()).ShouldContain(l => l.Name == "Synthetic/Raced");
    }

    [Fact]
    public async Task The_cache_belongs_to_the_connected_account()
    {
        await ListAsync();
        await h.Gmail.Inner.CreateLabelAsync("Synthetic/Other", Ct);
        var tokens = h.Services.GetRequiredService<FakeTokenStore>();

        await tokens.SaveAsync("someone@example.com", "synthetic-refresh-token", GmailScopes.All, Ct);
        (await ListAsync()).ShouldContain(l => l.Name == "Synthetic/Other");

        await tokens.DeleteAsync(Ct);
        (await h.GetAsync("/api/labels")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        await Should.ThrowAsync<GmailNotConnectedException>(() => h.Services.GetRequiredService<LabelCatalog>().GetAsync(Ct));
    }

    private async Task<List<LabelDto>> ListAsync()
    {
        var response = await h.GetAsync("/api/labels");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<LabelDto>>(Ct)).ShouldNotBeNull();
    }
}
