using GmailOrganiser.Common;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;
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

/// <summary>Google OAuth connect flow: start → Google consent → callback; status and disconnect.</summary>
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
        OAuthStateCookie stateCookie,
        GoogleClientService googleClient,
        IOptions<AppOptions> app,
        IOptions<GmailOptions> gmail,
        IOptions<GoogleOAuthOptions> oauth,
        CancellationToken ct)
    {
        var redirectUri = GoogleAuthorizationRequest.RedirectUri(app.Value.NormalisedBaseUrl);
        if (gmail.Value.UseFake)
        {
            // No Google: go straight to the callback, which still checks the state cookie.
            var fakeFlow = stateCookie.Issue(context);
            return TypedResults.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                redirectUri,
                new Dictionary<string, string?> { ["code"] = Fake.FakeGoogleOAuthClient.FakeCode, ["state"] = fakeFlow.State }));
        }

        var client = await googleClient.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Google client is not configured",
                detail: "Enter the Google OAuth client ID and secret in Settings (or .env) before connecting Gmail.");
        }

        var flow = stateCookie.Issue(context);
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
        var flow = stateCookie.Consume(context, state);
        var outcome = flow is null
            ? ConnectOutcome.StateMismatch
            : await connector.CompleteAsync(code, error, flow.CodeVerifier, ct);
        return TypedResults.Redirect(SetupUrl(app.Value, outcome));
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

    private static string SetupUrl(AppOptions app, ConnectOutcome outcome) =>
        outcome == ConnectOutcome.Connected
            ? $"{app.NormalisedBaseUrl}/setup?gmail=connected"
            : $"{app.NormalisedBaseUrl}/setup?gmail=error&reason={outcome.ToReason()}";
}
