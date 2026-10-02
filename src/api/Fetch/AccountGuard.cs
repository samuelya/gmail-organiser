using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Fetch;

public enum AccountCheckStatus
{
    /// <summary>The local data belongs to the connected account, or there is no connection to compare with.</summary>
    Ok,

    /// <summary>No fetch has recorded an account yet.</summary>
    NoLocalData,

    /// <summary>The local data belongs to a different account than the connected one.</summary>
    Mismatch,
}

/// <param name="LocalAccountMasked">The local data's account, masked; set only on <see cref="AccountCheckStatus.Mismatch"/>.</param>
public sealed record AccountCheck(AccountCheckStatus Status, string? LocalAccountMasked = null)
{
    public bool IsMismatch => Status == AccountCheckStatus.Mismatch;
}

/// <summary>
/// One Gmail account per install (DESIGN.md §2): fetching into another account's data is blocked, never merged.
/// Both sides are the Gmail profile address (<c>users.getProfile</c>): the token's account comes from
/// <see cref="Gmail.Auth.IGoogleOAuthClient.GetAccountEmailAsync"/>, the local one from <see cref="LocalAccountClaim"/>.
/// </summary>
public interface IAccountGuard
{
    /// <summary>Compares the local data's account with the connected one.</summary>
    Task<AccountCheck> CheckAsync(CancellationToken ct = default);

    /// <summary>As <see cref="CheckAsync(CancellationToken)"/>, with the connection the caller already loaded.</summary>
    Task<AccountCheck> CheckAsync(OAuthToken? connected, CancellationToken ct = default);

    /// <summary>
    /// True when connecting <paramref name="accountEmail"/> would switch accounts under a queued, running or paused job
    /// that reads Gmail (<see cref="FetchJobTypes.ReadsGmail"/>), or a failed apply or undo with a pending chunk. The
    /// account it would switch from is the local data's, else the connected one.
    /// </summary>
    Task<bool> RefusesConnectAsync(string accountEmail, CancellationToken ct = default);
}

/// <summary>Compares <c>fetch_state.account_email</c> with the connected account (trimmed, case-insensitive; dots and aliases kept).</summary>
public sealed partial class AccountGuard(AppDbContext db, ITokenStore tokens, ILogger<AccountGuard> logger) : IAccountGuard
{
    public const string ProblemType = "/problems/account-mismatch";
    public const string ProblemTitle = "Local data belongs to a different Gmail account";

    public const string ProblemDetail =
        "The stored mail was fetched from a different Gmail account than the one now connected. "
        + "Reconnect the original account, or purge the local data (available in Settings in a later release) before fetching.";

    /// <summary>The error recorded on a fetch job refused or stopped because the accounts differ.</summary>
    public const string RefuseReason = $"{ProblemTitle}. {ProblemDetail}";

    public async Task<AccountCheck> CheckAsync(CancellationToken ct = default) =>
        await CheckAsync(await tokens.GetAsync(ct), ct);

    public async Task<AccountCheck> CheckAsync(OAuthToken? connected, CancellationToken ct = default)
    {
        var check = Compare(await LocalAccountAsync(ct), connected?.AccountEmail);
        if (check.IsMismatch)
        {
            LogMismatch(logger, check.LocalAccountMasked!, Mask(connected!.AccountEmail));
        }

        return check;
    }

    public async Task<bool> RefusesConnectAsync(string accountEmail, CancellationToken ct = default)
    {
        var current = await LocalAccountAsync(ct);
        if (string.IsNullOrWhiteSpace(current))
        {
            current = (await tokens.GetAsync(ct))?.AccountEmail;
        }

        if (current is null || SameAccount(current, accountEmail))
        {
            return false;
        }

        if (await db.Jobs.AnyAsync(j => FetchJobTypes.ReadsGmail.Contains(j.Type) && JobRow.Active.Contains(j.Status), ct))
        {
            return true;
        }

        // A failed apply or undo with a pending chunk may have changed Gmail; only a resume on this account finishes it.
        return await db.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM jobs
            WHERE type = ANY({Review.ReviewJobTypes.WritesGmail}) AND status = {JobRow.FormatStatus(JobStatus.Failed)} AND cursor ->> 'pending' IS NOT NULL
            """).AnyAsync(ct);
    }

    /// <summary>
    /// The pure comparison, for callers that already loaded both sides. A reauth-required token still names its
    /// account; without a connection (<paramref name="connectedEmail"/> null) there is nothing to compare.
    /// </summary>
    public static AccountCheck Compare(string? localEmail, string? connectedEmail)
    {
        if (string.IsNullOrWhiteSpace(localEmail))
        {
            return new AccountCheck(AccountCheckStatus.NoLocalData);
        }

        return connectedEmail is null || SameAccount(localEmail, connectedEmail)
            ? new AccountCheck(AccountCheckStatus.Ok)
            : new AccountCheck(AccountCheckStatus.Mismatch, Mask(localEmail));
    }

    public static bool SameAccount(string a, string b) => Normalise(a) == Normalise(b);

    /// <summary>First character, <c>***</c>, then the full domain: <c>u***@example.com</c>.</summary>
    public static string Mask(string email)
    {
        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');
        var domain = at >= 0 ? trimmed[at..] : "";
        var first = at != 0 && trimmed.Length > 0 ? trimmed[..1] : "";
        return $"{first}***{domain}";
    }

    /// <summary>The 409 for a fetch start while mismatched; carries the masked local account as <c>localAccount</c>.</summary>
    public static IResult MismatchProblem(AccountCheck check) => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        type: ProblemType,
        title: ProblemTitle,
        detail: ProblemDetail,
        extensions: new Dictionary<string, object?> { ["localAccount"] = check.LocalAccountMasked });

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();

    private Task<string?> LocalAccountAsync(CancellationToken ct) => db.FetchState.AsNoTracking()
        .Where(s => s.Id == FetchStateRow.SingletonId)
        .Select(s => s.AccountEmail)
        .SingleOrDefaultAsync(ct);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetching is blocked: local data belongs to {LocalAccount}, connected account is {ConnectedAccount}")]
    private static partial void LogMismatch(ILogger logger, string localAccount, string connectedAccount);
}

public static class AccountGuardEndpointExtensions
{
    /// <summary>Answers <see cref="AccountGuard.MismatchProblem"/> instead of running the endpoint while the accounts differ.</summary>
    public static RouteHandlerBuilder RequireAccountMatch(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var guard = context.HttpContext.RequestServices.GetRequiredService<IAccountGuard>();
            var check = await guard.CheckAsync(context.HttpContext.RequestAborted);
            return check.IsMismatch ? AccountGuard.MismatchProblem(check) : await next(context);
        })
        .ProducesProblem(StatusCodes.Status409Conflict);
}

/// <summary>Fails a Gmail-reading job before it runs and at its next checkpoint while the accounts differ.</summary>
public sealed class FetchAccountJobGuard(IAccountGuard guard) : IJobRunGuard
{
    public async Task<string?> RefuseReasonAsync(CancellationToken ct) =>
        (await guard.CheckAsync(ct)).IsMismatch ? AccountGuard.RefuseReason : null;
}

/// <summary>
/// Records which account the local data belongs to. Every job that stores fetched mail calls <see cref="ClaimAsync"/>
/// with the Gmail profile address before its first write, so <c>fetch_state.account_email</c> is set whichever fetch
/// runs first.
/// </summary>
public sealed class LocalAccountClaim(AppDbContext db, TimeProvider time)
{
    /// <summary>
    /// Stamps <paramref name="profileEmail"/> when no account is recorded yet (atomic, so concurrent fetches agree).
    /// Throws <see cref="JobRefusedException"/> when the local data already belongs to another account.
    /// </summary>
    public async Task ClaimAsync(string profileEmail, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileEmail);
        var now = time.GetUtcNow();
        await db.FetchState
            .Where(s => s.Id == FetchStateRow.SingletonId && (s.AccountEmail == null || s.AccountEmail.Trim() == ""))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.AccountEmail, profileEmail).SetProperty(s => s.UpdatedAt, now), ct);
        var local = await db.FetchState.AsNoTracking()
            .Where(s => s.Id == FetchStateRow.SingletonId)
            .Select(s => s.AccountEmail)
            .SingleAsync(ct);
        if (local is null || !AccountGuard.SameAccount(local, profileEmail))
        {
            throw new JobRefusedException(AccountGuard.RefuseReason);
        }
    }
}
