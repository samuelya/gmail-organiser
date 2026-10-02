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

/// <summary>One Gmail account per install (DESIGN.md §2): fetching into another account's data is blocked, never merged.</summary>
public interface IAccountGuard
{
    Task<AccountCheck> CheckAsync(CancellationToken ct = default);
}

/// <summary>Compares <c>fetch_state.account_email</c> with the connected account (trimmed, case-insensitive; dots and aliases kept).</summary>
public sealed partial class AccountGuard(AppDbContext db, ITokenStore tokens, ILogger<AccountGuard> logger) : IAccountGuard
{
    public const string ProblemType = "/problems/account-mismatch";
    public const string ProblemTitle = "Local data belongs to a different Gmail account";

    public const string ProblemDetail =
        "The stored mail was fetched from a different Gmail account than the one now connected. "
        + "Reconnect the original account, or purge the local data (available in Settings in a later release) before fetching.";

    public async Task<AccountCheck> CheckAsync(CancellationToken ct = default)
    {
        var local = await db.FetchState.AsNoTracking()
            .Where(s => s.Id == FetchStateRow.SingletonId)
            .Select(s => s.AccountEmail)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(local))
        {
            return new AccountCheck(AccountCheckStatus.NoLocalData);
        }

        // A reauth-required token still names its account; without a connection there is nothing to compare.
        var connected = (await tokens.GetAsync(ct))?.AccountEmail;
        if (connected is null || Normalise(connected) == Normalise(local))
        {
            return new AccountCheck(AccountCheckStatus.Ok);
        }

        var masked = Mask(local);
        LogMismatch(logger, masked, Mask(connected));
        return new AccountCheck(AccountCheckStatus.Mismatch, masked);
    }

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

/// <summary>Fails a fetch-queue job instead of running it while the accounts differ, e.g. one queued before a reconnect.</summary>
public sealed class FetchAccountJobGuard(IAccountGuard guard) : IJobStartGuard
{
    public async Task<string?> RefuseReasonAsync(CancellationToken ct) =>
        (await guard.CheckAsync(ct)).IsMismatch ? $"{AccountGuard.ProblemTitle}. {AccountGuard.ProblemDetail}" : null;
}
