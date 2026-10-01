using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class TokenStoreTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string RefreshToken = "synthetic-refresh-token-1";
    private static readonly string[] Scopes = [.. GmailScopes.All];

    private readonly IDataProtectionProvider dataProtection = new EphemeralDataProtectionProvider();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.OAuthTokens.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Save_then_get_round_trips_the_connection()
    {
        await using var db = postgres.CreateDbContext();
        var store = CreateStore(db, dataProtection);

        await store.SaveAsync("user@example.com", RefreshToken, Scopes, Ct);
        var token = await store.GetAsync(Ct);

        token.ShouldNotBeNull();
        token.AccountEmail.ShouldBe("user@example.com");
        token.RefreshToken.ShouldBe(RefreshToken);
        token.Scopes.ShouldBe(Scopes);
        token.ConnectedAt.ShouldBe(token.UpdatedAt);
    }

    [Fact]
    public async Task Refresh_token_is_stored_as_ciphertext()
    {
        await using var db = postgres.CreateDbContext();
        await CreateStore(db, dataProtection).SaveAsync("user@example.com", RefreshToken, Scopes, Ct);

        var row = await db.OAuthTokens.AsNoTracking().SingleAsync(Ct);
        row.RefreshTokenProtected.ShouldNotBeNullOrWhiteSpace();
        row.RefreshTokenProtected.ShouldNotContain(RefreshToken);
        dataProtection.CreateProtector(TokenStore.ProtectorPurpose).Unprotect(row.RefreshTokenProtected).ShouldBe(RefreshToken);
    }

    [Fact]
    public async Task Save_again_replaces_the_token_and_keeps_connected_at_for_the_same_account()
    {
        await using var db = postgres.CreateDbContext();
        var store = CreateStore(db, dataProtection);
        await store.SaveAsync("user@example.com", RefreshToken, Scopes, Ct);
        var first = (await store.GetAsync(Ct))!;

        await store.SaveAsync("user@example.com", "synthetic-refresh-token-2", Scopes, Ct);
        var second = (await store.GetAsync(Ct))!;

        second.RefreshToken.ShouldBe("synthetic-refresh-token-2");
        second.ConnectedAt.ShouldBe(first.ConnectedAt);
        second.UpdatedAt.ShouldBeGreaterThanOrEqualTo(first.UpdatedAt);
        (await db.OAuthTokens.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Delete_forgets_the_connection_and_is_idempotent()
    {
        await using var db = postgres.CreateDbContext();
        var store = CreateStore(db, dataProtection);
        await store.SaveAsync("user@example.com", RefreshToken, Scopes, Ct);

        await store.DeleteAsync(Ct);
        await store.DeleteAsync(Ct);

        (await store.GetAsync(Ct)).ShouldBeNull();
        (await db.OAuthTokens.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Undecryptable_token_reads_as_not_connected()
    {
        await using var db = postgres.CreateDbContext();
        await CreateStore(db, dataProtection).SaveAsync("user@example.com", RefreshToken, Scopes, Ct);

        var otherKeys = CreateStore(db, new EphemeralDataProtectionProvider());

        (await otherKeys.GetAsync(Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Gmail_fake_registers_the_fake_client_and_a_connected_token_store()
    {
        using var fake = factory.WithWebHostBuilder(b => b.UseSetting(GmailOptions.FakeEnvironmentKey, "true"));
        await using var scope = fake.Services.CreateAsyncScope();

        var client = scope.ServiceProvider.GetRequiredService<IGmailClient>();
        var token = await scope.ServiceProvider.GetRequiredService<ITokenStore>().GetAsync(Ct);

        client.ShouldBeOfType<FakeGmailClient>();
        (await client.GetProfileAsync(Ct)).EmailAddress.ShouldBe("user@example.com");
        token.ShouldNotBeNull();
        token.AccountEmail.ShouldBe("user@example.com");
        await using var db = postgres.CreateDbContext();
        (await db.OAuthTokens.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Without_gmail_fake_the_google_client_reports_not_connected()
    {
        await using var scope = factory.Services.CreateAsyncScope();

        var client = scope.ServiceProvider.GetRequiredService<IGmailClient>();

        client.ShouldBeOfType<GoogleGmailClient>();
        scope.ServiceProvider.GetRequiredService<ITokenStore>().ShouldBeOfType<TokenStore>();
        await Should.ThrowAsync<GmailNotConnectedException>(() => client.GetProfileAsync(Ct));
    }

    private static TokenStore CreateStore(AppDbContext db, IDataProtectionProvider protection) =>
        new(db, protection, TimeProvider.System, NullLogger<TokenStore>.Instance);
}
