using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Gmail.Auth;

public enum ConnectOutcome
{
    Connected,
    StateMismatch,
    MissingScopes,
    AccessDenied,
    ExchangeFailed,

    /// <summary>A different account while a Gmail-reading job is active (<see cref="IAccountGuard.RefusesConnectAsync"/>).</summary>
    FetchActive,
}

public static class ConnectOutcomeExtensions
{
    /// <summary>The <c>reason</c> query value the setup page receives.</summary>
    public static string ToReason(this ConnectOutcome outcome) => outcome switch
    {
        ConnectOutcome.StateMismatch => "state_mismatch",
        ConnectOutcome.MissingScopes => "missing_scopes",
        ConnectOutcome.AccessDenied => "access_denied",
        ConnectOutcome.ExchangeFailed => "exchange_failed",
        ConnectOutcome.FetchActive => "fetch_active",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}

/// <summary>
/// Completes and removes the Gmail connection. Nothing is stored unless the code exchange succeeded, all scopes were
/// granted and the account email is known, and never for another account while a fetch is active. Never logs codes,
/// tokens or the client secret.
/// </summary>
public sealed class GmailConnector(
    IGoogleOAuthClient oauth,
    ITokenStore tokens,
    IAccountGuard accountGuard,
    GoogleClientService googleClient,
    IOptions<AppOptions> app,
    IOptions<GmailOptions> gmail,
    IOptions<GoogleOAuthOptions> oauthOptions,
    TimeProvider time,
    ILogger<GmailConnector> logger)
{
    public async Task<ConnectOutcome> CompleteAsync(string? code, string? error, string codeVerifier, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            logger.LogInformation("Google consent was not granted");
            return ConnectOutcome.AccessDenied;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return ConnectOutcome.ExchangeFailed;
        }

        var client = await googleClient.GetAsync(ct);
        if (!gmail.Value.UseFake && (string.IsNullOrWhiteSpace(client.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret)))
        {
            logger.LogWarning("Google OAuth callback received but no Google client is configured");
            return ConnectOutcome.ExchangeFailed;
        }

        GoogleTokenResult result;
        string accountEmail;
        try
        {
            result = await oauth.ExchangeCodeAsync(
                new GoogleCodeExchange(
                    code,
                    codeVerifier,
                    GoogleAuthorizationRequest.RedirectUri(app.Value.NormalisedBaseUrl),
                    client.ClientId ?? "",
                    client.ClientSecret ?? ""),
                ct);

            var missing = GmailScopes.All.Except(result.Scopes, StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                logger.LogWarning("Google connection refused: {MissingScopeCount} required scope(s) not granted ({MissingScopes})",
                    missing.Length, string.Join(' ', missing));
                return ConnectOutcome.MissingScopes;
            }

            if (result.RefreshToken is null)
            {
                logger.LogWarning("Google issued no refresh token");
                return ConnectOutcome.ExchangeFailed;
            }

            accountEmail = await oauth.GetAccountEmailAsync(result.AccessToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Google code exchange failed: {ExceptionType} {GoogleError}",
                ex.GetType().Name, (ex as GoogleOAuthException)?.Error);
            return ConnectOutcome.ExchangeFailed;
        }

        // The Gmail client reads the token on every call, so a switch now would merge another account's mail mid-run.
        if (await accountGuard.RefusesConnectAsync(accountEmail, ct))
        {
            logger.LogWarning("Google connection refused: a different account while a fetch job is active");
            return ConnectOutcome.FetchActive;
        }

        await tokens.SaveAsync(accountEmail, result.RefreshToken, result.Scopes, ct);
        logger.LogInformation("Gmail connected");
        return ConnectOutcome.Connected;
    }

    /// <summary>
    /// Deletes the local token first, so it is gone even if the request is aborted, then revokes at Google best effort
    /// with its own timeout. The revoke deliberately ignores the caller's cancellation: the token is already deleted
    /// locally, and an aborted request should still invalidate it at Google if Google answers in time.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        await tokens.DeleteAsync(CancellationToken.None);
        logger.LogInformation("Gmail disconnected");
        if (token is null)
        {
            return;
        }

        using var revokeTimeout = new CancellationTokenSource(oauthOptions.Value.RevokeTimeout, time);
        try
        {
            await oauth.RevokeAsync(token.RefreshToken, revokeTimeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Revoking the Gmail token at Google failed ({ExceptionType}); it was deleted locally", ex.GetType().Name);
        }
    }
}
