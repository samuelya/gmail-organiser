using GmailOrganiser.Gmail;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The real <see cref="GoogleGmailClient"/> is scoped and its token and settings reads share the scope's one
/// <c>AppDbContext</c>; the analysis run calls it from parallel body fetches. No call here reaches Google.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GoogleGmailClientConcurrencyTests(ApiFactory factory, PostgresFixture postgres)
    : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const int Callers = 4;
    private const int Rounds = 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await ResetAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TokenStore>()
            .SaveAsync("user@example.com", "synthetic-refresh-token", [.. GmailScopes.All], Ct);
        (await scope.ServiceProvider.GetRequiredService<GoogleClientService>()
            .TrySetAsync("synthetic-client-id.example.com", "synthetic-client-secret", Ct)).ShouldBeTrue();
    }

    public async ValueTask DisposeAsync() => await ResetAsync();

    [Fact]
    public async Task Concurrent_service_creation_in_one_scope_succeeds()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<GoogleGmailClient>();

        for (var round = 0; round < Rounds; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => Task.Run(async () =>
            {
                var (flow, service) = await client.CreateServiceAsync(Ct);
                flow.Dispose();
                service.Dispose();
            }, Ct)));
        }
    }

    [Fact]
    public async Task Concurrent_reauth_marks_and_service_creation_in_one_scope_do_not_overlap()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<GoogleGmailClient>();

        for (var round = 0; round < Rounds; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, Callers).Select(i => Task.Run(async () =>
            {
                if (i % 2 == 0)
                {
                    await client.MarkReauthRequiredAsync();
                    return;
                }

                try
                {
                    var (flow, service) = await client.CreateServiceAsync(Ct);
                    flow.Dispose();
                    service.Dispose();
                }
                catch (GmailNotConnectedException)
                {
                    // Expected once a concurrent caller has flagged reauth; any DbContext error fails the test.
                }
            }, Ct)));
        }

        (await scope.ServiceProvider.GetRequiredService<TokenStore>().GetAsync(Ct))!.ReauthRequired.ShouldBeTrue();
    }

    private async Task ResetAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.OAuthTokens.ExecuteDeleteAsync(Ct);
        await db.Settings.ExecuteDeleteAsync(Ct);
    }
}
