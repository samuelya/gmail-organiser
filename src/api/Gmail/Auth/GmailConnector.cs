using GmailOrganiser.Common;
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
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}

/// <summary>
/// Completes and removes the Gmail connection. Nothing is stored unless the code exchange succeeded, all scopes were
/// granted and the account email is known. Never logs codes, tokens or the client secret.
/// </summary>
public sealed class GmailConnector(
    IGoogleOAuthClient oauth,
    ITokenStore tokens,
    GoogleClientService googleClient,
    IOptions<AppOptions> app,
    IOptions<GmailOptions> gmail,
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

        await tokens.SaveAsync(accountEmail, result.RefreshToken, result.Scopes, ct);
        logger.LogInformation("Gmail connected");
        return ConnectOutcome.Connected;
    }

    /// <summary>Revokes at Google (best effort, logged on failure), then always deletes the local token.</summary>
    public async Task DisconnectAsync(CancellationToken ct)
    {
        var token = await tokens.GetAsync(ct);
        if (token is not null)
        {
            try
            {
                await oauth.RevokeAsync(token.RefreshToken, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Revoking the Gmail token at Google failed ({ExceptionType}); deleting it locally anyway", ex.GetType().Name);
            }
        }

        await tokens.DeleteAsync(ct);
        logger.LogInformation("Gmail disconnected");
    }
}
