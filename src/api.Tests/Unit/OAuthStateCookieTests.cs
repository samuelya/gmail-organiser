using GmailOrganiser.Gmail.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

public sealed class OAuthStateCookieTests
{
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly OAuthStateCookie cookie;
    private readonly IDataProtector protector;

    public OAuthStateCookieTests()
    {
        var dataProtection = new EphemeralDataProtectionProvider();
        cookie = new OAuthStateCookie(dataProtection, time, Options.Create(new GoogleOAuthOptions()));
        protector = dataProtection.CreateProtector(OAuthStateCookie.ProtectorPurpose);
    }

    [Fact]
    public void Issued_cookie_is_httponly_lax_scoped_and_short_lived()
    {
        var context = new DefaultHttpContext();
        cookie.Issue(context, OAuthReturnTo.Setup);

        var header = context.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        header.ShouldStartWith(OAuthStateCookie.CookieName + "=");
        header.ShouldContain("httponly");
        header.ShouldContain("samesite=lax");
        header.ShouldContain("path=/api/auth/google");
        header.ShouldContain("max-age=600");
    }

    [Fact]
    public void Matching_state_returns_the_flow()
    {
        var (flow, callback) = IssueAndReturn();

        cookie.Consume(callback, flow.State).ShouldBe(new OAuthConsumeResult(flow, OAuthReturnTo.Setup));
    }

    [Fact]
    public void Return_target_round_trips_in_the_cookie()
    {
        var (flow, callback) = IssueAndReturn(OAuthReturnTo.Settings);

        var result = cookie.Consume(callback, flow.State);

        result.Flow.ShouldBe(flow);
        result.ReturnTo.ShouldBe(OAuthReturnTo.Settings);
    }

    [Fact]
    public void Invalid_flow_still_returns_the_decrypted_target()
    {
        var (_, wrongState) = IssueAndReturn(OAuthReturnTo.Settings);
        cookie.Consume(wrongState, "other-state").ShouldBe(new OAuthConsumeResult(null, OAuthReturnTo.Settings));

        var (_, noState) = IssueAndReturn(OAuthReturnTo.Settings);
        cookie.Consume(noState, null).ShouldBe(new OAuthConsumeResult(null, OAuthReturnTo.Settings));

        var (flow, expired) = IssueAndReturn(OAuthReturnTo.Settings);
        time.Advance(TimeSpan.FromMinutes(10));
        cookie.Consume(expired, flow.State).ShouldBe(new OAuthConsumeResult(null, OAuthReturnTo.Settings));
    }

    [Fact]
    public void Cookie_without_a_return_target_reads_as_setup()
    {
        var expiresAt = time.GetUtcNow().AddMinutes(5);
        var legacy = protector.Protect($$"""{"State":"s","CodeVerifier":"v","ExpiresAt":"{{expiresAt:O}}"}""");
        var callback = new DefaultHttpContext();
        callback.Request.Headers.Cookie = $"{OAuthStateCookie.CookieName}={legacy}";

        var result = cookie.Consume(callback, "s");

        result.Flow.ShouldNotBeNull();
        result.ReturnTo.ShouldBe(OAuthReturnTo.Setup);
    }

    [Fact]
    public void Unknown_return_target_number_reads_as_setup()
    {
        var expiresAt = time.GetUtcNow().AddMinutes(5);
        var payload = protector.Protect($$"""{"State":"s","CodeVerifier":"v","ExpiresAt":"{{expiresAt:O}}","ReturnTo":7}""");
        var callback = new DefaultHttpContext();
        callback.Request.Headers.Cookie = $"{OAuthStateCookie.CookieName}={payload}";

        cookie.Consume(callback, "s").ReturnTo.ShouldBe(OAuthReturnTo.Setup);
    }

    [Fact]
    public void Wrong_or_missing_state_is_rejected()
    {
        var (_, callback) = IssueAndReturn();
        cookie.Consume(callback, "other-state").Flow.ShouldBeNull();
        cookie.Consume(callback, null).Flow.ShouldBeNull();
        cookie.Consume(new DefaultHttpContext(), "any").ShouldBe(new OAuthConsumeResult(null, OAuthReturnTo.Setup));
    }

    [Fact]
    public void Expired_flow_is_rejected()
    {
        var (flow, callback) = IssueAndReturn();
        time.Advance(TimeSpan.FromMinutes(10));

        cookie.Consume(callback, flow.State).Flow.ShouldBeNull();
    }

    [Fact]
    public void Tampered_cookie_is_rejected_and_the_cookie_is_cleared()
    {
        var callback = new DefaultHttpContext();
        callback.Request.Headers.Cookie = $"{OAuthStateCookie.CookieName}=not-a-protected-payload";

        cookie.Consume(callback, "state").ShouldBe(new OAuthConsumeResult(null, OAuthReturnTo.Setup));
        callback.Response.Headers.SetCookie.ToString().ShouldContain("expires=Thu, 01 Jan 1970");
    }

    private (OAuthPendingFlow Flow, HttpContext Callback) IssueAndReturn(OAuthReturnTo returnTo = OAuthReturnTo.Setup)
    {
        var start = new DefaultHttpContext();
        var flow = cookie.Issue(start, returnTo);
        var pair = start.Response.Headers.SetCookie.ToString().Split(';')[0];
        var callback = new DefaultHttpContext();
        callback.Request.Headers.Cookie = pair;
        return (flow, callback);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
