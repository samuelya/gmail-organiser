using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Auth;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class GmailAuthEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string ClientId = "test-client.apps.googleusercontent.com";
    private const string Base = "/api/auth/google";
    private static readonly string SetupUrl = ApiFactory.AllowedOrigin + "/setup";

    private readonly StubGoogleOAuthClient oauth = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.OAuthTokens.ExecuteDeleteAsync(Ct);
        await db.Settings.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Start_without_a_google_client_is_409()
    {
        using var client = CreateClient(factory);

        var response = await client.GetAsync($"{Base}/start", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Start_redirects_to_google_and_sets_the_state_cookie()
    {
        await using var host = WithGoogle();
        using var client = CreateClient(host);

        var response = await client.GetAsync($"{Base}/start", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).ShouldBe(new GoogleOAuthOptions().AuthorizationEndpoint);
        var query = QueryHelpers.ParseQuery(location.Query);
        query["client_id"].ToString().ShouldBe(ClientId);
        query["redirect_uri"].ToString().ShouldBe(ApiFactory.AllowedOrigin + "/api/auth/google/callback");
        query["code_challenge_method"].ToString().ShouldBe("S256");
        query["state"].ToString().ShouldNotBeNullOrEmpty();
        var cookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        cookie.ShouldContain("httponly");
        cookie.ShouldContain("samesite=lax");
    }

    [Fact]
    public async Task Callback_with_a_bad_state_redirects_with_state_mismatch()
    {
        await using var host = WithGoogle();
        using var client = CreateClient(host);
        await StartAsync(client);

        var response = await client.GetAsync($"{Base}/callback?code=c&state=forged", Ct);

        response.Headers.Location!.ToString().ShouldBe(SetupUrl + "?gmail=error&reason=state_mismatch");
        oauth.Exchanges.ShouldBeEmpty();
        (await GetStatusAsync(client)).Connected.ShouldBeFalse();
    }

    [Fact]
    public async Task Callback_without_the_cookie_redirects_with_state_mismatch()
    {
        await using var host = WithGoogle();
        using var client = CreateClient(host);

        var response = await client.GetAsync($"{Base}/callback?code=c&state=s", Ct);

        response.Headers.Location!.ToString().ShouldBe(SetupUrl + "?gmail=error&reason=state_mismatch");
    }

    [Fact]
    public async Task Callback_with_missing_scopes_stores_nothing()
    {
        oauth.Result = oauth.Result with { Scopes = [GmailScopes.Modify, GmailScopes.Labels] };
        await using var host = WithGoogle();
        using var client = CreateClient(host);

        var location = await CallbackAsync(client, await StartAsync(client));

        location.ShouldBe(SetupUrl + "?gmail=error&reason=missing_scopes");
        var status = await GetStatusAsync(client);
        status.Connected.ShouldBeFalse();
        status.Reason.ShouldBe(GmailAuthEndpoints.ReasonNotConnected);
    }

    [Fact]
    public async Task Callback_with_access_denied_redirects_with_access_denied()
    {
        await using var host = WithGoogle();
        using var client = CreateClient(host);
        var state = await StartAsync(client);

        var response = await client.GetAsync($"{Base}/callback?error=access_denied&state={state}", Ct);

        response.Headers.Location!.ToString().ShouldBe(SetupUrl + "?gmail=error&reason=access_denied");
        oauth.Exchanges.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("google_error")]
    [InlineData("timeout")]
    public async Task Callback_when_the_exchange_fails_redirects_with_exchange_failed(string failure)
    {
        // HttpClient reports a timeout as TaskCanceledException while the request's own token is not cancelled.
        oauth.ExchangeException = failure == "timeout"
            ? new TaskCanceledException("timed out", new TimeoutException())
            : new GoogleOAuthException("refused", "invalid_grant");
        await using var host = WithGoogle();
        using var client = CreateClient(host);

        (await CallbackAsync(client, await StartAsync(client))).ShouldBe(SetupUrl + "?gmail=error&reason=exchange_failed");
    }

    [Fact]
    public async Task Successful_callback_stores_the_token_and_status_reports_connected()
    {
        await using var host = WithGoogle();
        using var client = CreateClient(host);

        var location = await CallbackAsync(client, await StartAsync(client));

        location.ShouldBe(SetupUrl + "?gmail=connected");
        var exchange = oauth.Exchanges.Single();
        exchange.Code.ShouldBe("synthetic-code");
        exchange.ClientId.ShouldBe(ClientId);
        exchange.RedirectUri.ShouldBe(ApiFactory.AllowedOrigin + "/api/auth/google/callback");
        exchange.CodeVerifier.Length.ShouldBe(43);

        var status = await GetStatusAsync(client);
        status.Connected.ShouldBeTrue();
        status.Reason.ShouldBeNull();
        status.AccountEmail.ShouldBe("user@example.com");
        status.Scopes.ShouldBe(GmailScopes.All, ignoreOrder: true);
        status.MissingScopes.ShouldBeEmpty();
        status.RedirectUri.ShouldBe(ApiFactory.AllowedOrigin + "/api/auth/google/callback");
    }

    [Fact]
    public async Task Reused_state_is_rejected()
    {
        await using var host = WithGoogle();
        using var client = CreateClient(host);
        var state = await StartAsync(client);
        await CallbackAsync(client, state);

        (await CallbackAsync(client, state)).ShouldBe(SetupUrl + "?gmail=error&reason=state_mismatch");
    }

    [Fact]
    public async Task Status_reports_reauth_required_after_invalid_grant_until_reconnected()
    {
        await using var host = WithGoogle();
        using var client = CreateClient(host);
        await CallbackAsync(client, await StartAsync(client));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITokenStore>().MarkReauthRequiredAsync(Ct);
        }

        var status = await GetStatusAsync(client);
        status.Connected.ShouldBeFalse();
        status.Reason.ShouldBe(GmailAuthEndpoints.ReasonReauthRequired);
        status.AccountEmail.ShouldBe("user@example.com");

        await CallbackAsync(client, await StartAsync(client));
        (await GetStatusAsync(client)).Connected.ShouldBeTrue();
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task Disconnect_deletes_the_token_even_if_revoke_throws(string failure)
    {
        oauth.RevokeException = failure == "timeout"
            ? new TaskCanceledException("timed out", new TimeoutException())
            : new HttpRequestException("network down");
        await using var host = WithGoogle();
        using var client = CreateClient(host);
        await CallbackAsync(client, await StartAsync(client));

        var response = await PostDisconnectAsync(client);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        oauth.RevokedTokens.ShouldBe(["synthetic-refresh-token"]);
        (await GetStatusAsync(client)).Connected.ShouldBeFalse();
        await using var db = postgres.CreateDbContext();
        (await db.OAuthTokens.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Disconnect_requires_the_anti_csrf_header()
    {
        using var client = CreateClient(factory);

        var response = await client.PostAsync($"{Base}/disconnect", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Fake_mode_runs_the_whole_flow_without_google()
    {
        await using var host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true"));
        using var client = CreateClient(host);
        (await PostDisconnectAsync(client)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetStatusAsync(client)).Connected.ShouldBeFalse();

        var start = await client.GetAsync($"{Base}/start", Ct);
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var callback = start.Headers.Location!;
        callback.GetLeftPart(UriPartial.Path).ShouldBe(ApiFactory.AllowedOrigin + "/api/auth/google/callback");

        var done = await client.GetAsync(callback.PathAndQuery, Ct);

        done.Headers.Location!.ToString().ShouldBe(SetupUrl + "?gmail=connected");
        var status = await GetStatusAsync(client);
        status.Connected.ShouldBeTrue();
        status.AccountEmail.ShouldBe("user@example.com");
        status.MissingScopes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("RevokeTimeout", "-00:00:05")]
    [InlineData("RevokeTimeout", "-00:00:00.001")]
    [InlineData("RevokeTimeout", "00:00:00")]
    [InlineData("RevokeTimeout", "01:00:00")]
    [InlineData("StateLifetime", "00:00:00")]
    [InlineData("StateLifetime", "-00:10:00")]
    public void Start_up_fails_for_an_invalid_oauth_time_span(string option, string value)
    {
        using var badFactory = new ApiFactory(postgres).WithWebHostBuilder(b => b.UseSetting($"{GoogleOAuthOptions.SectionName}:{option}", value));

        var error = Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => badFactory.CreateClient());
        error.Message.ShouldContain(option);
    }

    private WebApplicationFactory<Program> WithGoogle() => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("GOOGLE_CLIENT_ID", ClientId);
        b.UseSetting("GOOGLE_CLIENT_SECRET", "synthetic-secret-value");
        b.ConfigureTestServices(s => s.AddScoped<IGoogleOAuthClient>(_ => oauth));
    });

    private static HttpClient CreateClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <returns>The state sent to Google.</returns>
    private static async Task<string> StartAsync(HttpClient client)
    {
        var response = await client.GetAsync($"{Base}/start", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
    }

    private static async Task<string> CallbackAsync(HttpClient client, string state)
    {
        var response = await client.GetAsync($"{Base}/callback?code=synthetic-code&state={Uri.EscapeDataString(state)}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return response.Headers.Location!.ToString();
    }

    private static async Task<GmailConnectionStatusDto> GetStatusAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<GmailConnectionStatusDto>($"{Base}/status", Ct))!;

    private static Task<HttpResponseMessage> PostDisconnectAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/disconnect");
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return client.SendAsync(request, Ct);
    }
}
