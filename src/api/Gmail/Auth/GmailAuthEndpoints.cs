using GmailOrganiser.Common;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail.Auth;

/// <param name="Reason"><c>not_connected</c> or <c>reauth_required</c> when <paramref name="Connected"/> is false.</param>
/// <param name="RedirectUri">The URI to register in the Google OAuth client.</param>
public sealed record GmailConnectionStatusDto(
    bool Connected,
    string? Reason,
    string? AccountEmail,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> MissingScopes,
    string RedirectUri);

/// <summary>
/// Google OAuth connect flow: start → Google consent → callback; status and disconnect. <c>/start?returnTo=settings</c>
/// makes the callback return to <c>/settings</c>; any other value returns to <c>/setup</c>. Either way the page gets
/// <c>?gmail=connected</c> or <c>?gmail=error&amp;reason=…</c>.
/// </summary>
public static class GmailAuthEndpoints
{
    public const string ReasonNotConnected = "not_connected";
    public const string ReasonReauthRequired = "reauth_required";

    public static IEndpointRouteBuilder MapGmailAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth/google").WithTags("Gmail auth");
        group.MapGet("/start", StartAsync);
        group.MapGet("/callback", CallbackAsync);
        group.MapGet("/status", StatusAsync);
        group.MapPost("/disconnect", DisconnectAsync);
        return endpoints;
    }

    private static async Task<Results<RedirectHttpResult, ProblemHttpResult>> StartAsync(
        HttpContext context,
        string? returnTo,
        OAuthStateCookie stateCookie,
        GoogleClientService googleClient,
        IOptions<AppOptions> app,
        IOptions<GmailOptions> gmail,
        IOptions<GoogleOAuthOptions> oauth,
        CancellationToken ct)
    {
        var redirectUri = GoogleAuthorizationRequest.RedirectUri(app.Value.NormalisedBaseUrl);
        var target = ParseReturnTo(returnTo);
        if (gmail.Value.UseFake)
        {
            // No Google: go straight to the callback, which still checks the state cookie.
            var fakeFlow = stateCookie.Issue(context, target);
            return TypedResults.Redirect(QueryHelpers.AddQueryString(
                redirectUri,
                new Dictionary<string, string?> { ["code"] = FakeGoogleOAuthClient.FakeCode, ["state"] = fakeFlow.State }));
        }

        var client = await googleClient.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Google client is not configured",
                detail: "Enter the Google OAuth client ID and secret in Settings (or .env) before connecting Gmail.");
        }

        var flow = stateCookie.Issue(context, target);
        return TypedResults.Redirect(GoogleAuthorizationRequest.BuildUrl(
            oauth.Value.AuthorizationEndpoint, client.ClientId, redirectUri, flow.State, flow.CodeVerifier));
    }

    private static async Task<RedirectHttpResult> CallbackAsync(
        HttpContext context,
        string? code,
        string? state,
        string? error,
        GmailConnector connector,
        OAuthStateCookie stateCookie,
        IOptions<AppOptions> app,
        CancellationToken ct)
    {
        var (flow, returnTo) = stateCookie.Consume(context, state);
        var outcome = flow is null
            ? ConnectOutcome.StateMismatch
            : await connector.CompleteAsync(code, error, flow.CodeVerifier, ct);
        return TypedResults.Redirect(ReturnUrl(app.Value, returnTo, outcome));
    }

    private static async Task<Ok<GmailConnectionStatusDto>> StatusAsync(
        ITokenStore tokens, IOptions<AppOptions> app, CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        var granted = token?.Scopes ?? [];
        var connected = token is not null && !token.ReauthRequired;
        return TypedResults.Ok(new GmailConnectionStatusDto(
            connected,
            connected ? null : token is null ? ReasonNotConnected : ReasonReauthRequired,
            token?.AccountEmail,
            granted,
            [.. GmailScopes.All.Except(granted, StringComparer.Ordinal)],
            GoogleAuthorizationRequest.RedirectUri(app.Value.NormalisedBaseUrl)));
    }

    private static async Task<NoContent> DisconnectAsync(GmailConnector connector, CancellationToken ct)
    {
        await connector.DisconnectAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Exact, ordinal match only: anything else (case variants, paths, URLs, empty) is Setup.</summary>
    private static OAuthReturnTo ParseReturnTo(string? returnTo) => returnTo switch
    {
        "settings" => OAuthReturnTo.Settings,
        _ => OAuthReturnTo.Setup,
    };

    /// <summary>Built from the enum only, never from request input, so it can't be an open redirect.</summary>
    private static string ReturnUrl(AppOptions app, OAuthReturnTo returnTo, ConnectOutcome outcome)
    {
        var path = returnTo == OAuthReturnTo.Settings ? "/settings" : "/setup";
        return outcome == ConnectOutcome.Connected
            ? $"{app.NormalisedBaseUrl}{path}?gmail=connected"
            : $"{app.NormalisedBaseUrl}{path}?gmail=error&reason={outcome.ToReason()}";
    }
}
