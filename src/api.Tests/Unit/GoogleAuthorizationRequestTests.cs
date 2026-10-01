using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Auth;
using Microsoft.AspNetCore.WebUtilities;

namespace GmailOrganiser.Tests.Unit;

public sealed class GoogleAuthorizationRequestTests
{
    [Fact]
    public void Url_carries_scopes_offline_consent_pkce_and_state()
    {
        var verifier = GoogleAuthorizationRequest.NewRandomValue();
        var url = new Uri(GoogleAuthorizationRequest.BuildUrl(
            "https://auth.example.com/authorize", "client-1.example.com", "http://app.example.com/api/auth/google/callback", "state-1", verifier));

        url.GetLeftPart(UriPartial.Path).ShouldBe("https://auth.example.com/authorize");
        var query = QueryHelpers.ParseQuery(url.Query);
        query["client_id"].ToString().ShouldBe("client-1.example.com");
        query["redirect_uri"].ToString().ShouldBe("http://app.example.com/api/auth/google/callback");
        query["response_type"].ToString().ShouldBe("code");
        query["scope"].ToString().Split(' ').ShouldBe(GmailScopes.All, ignoreOrder: true);
        query["scope"].ToString().ShouldNotContain("mail.google.com");
        query["access_type"].ToString().ShouldBe("offline");
        query["prompt"].ToString().ShouldBe("consent");
        query["include_granted_scopes"].ToString().ShouldBe("true");
        query["state"].ToString().ShouldBe("state-1");
        query["code_challenge_method"].ToString().ShouldBe("S256");
        query["code_challenge"].ToString().ShouldBe(GoogleAuthorizationRequest.ChallengeFor(verifier));
    }

    [Fact]
    public void Challenge_is_base64url_sha256_of_the_verifier()
    {
        // Expected value computed independently: base64url(sha256(ascii(verifier))) without padding.
        GoogleAuthorizationRequest.ChallengeFor("dBjftJeZ4CVP-mJ92IGIp5Jvh4yqTk9WlxoRYjxNoQ")
            .ShouldBe("nhNznwRDdf4LsLyqj92VihTZdlIQ-jMtTwMWDlvHpYE");
    }

    [Fact]
    public void Random_values_are_url_safe_and_unique()
    {
        var a = GoogleAuthorizationRequest.NewRandomValue();
        var b = GoogleAuthorizationRequest.NewRandomValue();

        a.ShouldNotBe(b);
        a.Length.ShouldBe(43);
        a.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    }

    [Fact]
    public void Redirect_uri_is_built_from_the_app_base_url() =>
        GoogleAuthorizationRequest.RedirectUri("http://app.example.com:5180/")
            .ShouldBe("http://app.example.com:5180/api/auth/google/callback");
}
