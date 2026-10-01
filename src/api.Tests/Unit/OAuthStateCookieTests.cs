using GmailOrganiser.Gmail.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

public sealed class OAuthStateCookieTests
{
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly OAuthStateCookie cookie;

    public OAuthStateCookieTests() =>
        cookie = new OAuthStateCookie(new EphemeralDataProtectionProvider(), time, Options.Create(new GoogleOAuthOptions()));

    [Fact]
    public void Issued_cookie_is_httponly_lax_scoped_and_short_lived()
    {
        var context = new DefaultHttpContext();
        cookie.Issue(context);

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

        cookie.Consume(callback, flow.State).ShouldBe(flow);
    }

    [Fact]
    public void Wrong_or_missing_state_is_rejected()
    {
        var (_, callback) = IssueAndReturn();
        cookie.Consume(callback, "other-state").ShouldBeNull();
        cookie.Consume(callback, null).ShouldBeNull();
        cookie.Consume(new DefaultHttpContext(), "any").ShouldBeNull();
    }

    [Fact]
    public void Expired_flow_is_rejected()
    {
        var (flow, callback) = IssueAndReturn();
        time.Advance(TimeSpan.FromMinutes(10));

        cookie.Consume(callback, flow.State).ShouldBeNull();
    }

    [Fact]
    public void Tampered_cookie_is_rejected_and_the_cookie_is_cleared()
    {
        var callback = new DefaultHttpContext();
        callback.Request.Headers.Cookie = $"{OAuthStateCookie.CookieName}=not-a-protected-payload";

        cookie.Consume(callback, "state").ShouldBeNull();
        callback.Response.Headers.SetCookie.ToString().ShouldContain("expires=Thu, 01 Jan 1970");
    }

    private (OAuthPendingFlow Flow, HttpContext Callback) IssueAndReturn()
    {
        var start = new DefaultHttpContext();
        var flow = cookie.Issue(start);
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
