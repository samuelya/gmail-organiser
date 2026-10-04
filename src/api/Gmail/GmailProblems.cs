using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Gmail;

/// <summary>The problem responses endpoints answer when Gmail can't be reached.</summary>
public static class GmailProblems
{
    public static ProblemHttpResult NotConnected(GmailNotConnectedException ex) =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Gmail not connected", detail: ex.Message);

    public static ProblemHttpResult RateLimited(GmailRateLimitedException ex) =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Gmail is rate-limiting requests", detail: ex.Message);
}
