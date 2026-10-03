using System.Text.Json;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Common;
using GmailOrganiser.Mcp;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace GmailOrganiser.Claude;

/// <summary>The Claude review queue: send items, list them, cancel, retry, accept or dismiss a verdict. No Gmail calls.</summary>
public static class ClaudeReviewEndpoints
{
    public static IEndpointRouteBuilder MapClaudeReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/claude/reviews").WithTags("Claude");
        group.MapPost("", CreateAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/summary", (ExternalReviewQuery query, CancellationToken ct) => query.SummaryAsync(ct));
        group.MapGet("/{id:guid}", async Task<Results<Ok<ExternalReviewDto>, NotFound>> (Guid id, ExternalReviewQuery query, CancellationToken ct) =>
            await query.GetAsync(id, ct) is { } item ? TypedResults.Ok(item) : TypedResults.NotFound());
        group.MapPost("/{id:guid}/cancel", (Guid id, ExternalReviewService reviews, CancellationToken ct) =>
            ToResultAsync(reviews.CancelAsync(id, ct)));
        group.MapPost("/{id:guid}/accept", (Guid id, ExternalReviewService reviews, CancellationToken ct) =>
            ToResultAsync(reviews.AcceptAsync(id, ct)));
        group.MapPost("/{id:guid}/dismiss", (Guid id, ExternalReviewService reviews, CancellationToken ct) =>
            ToResultAsync(reviews.DismissAsync(id, ct)));
        group.MapPost("/{id:guid}/retry", (Guid id, ExternalReviewService reviews, CancellationToken ct) =>
            ToResultAsync(reviews.RetryAsync(id, ct)));
        endpoints.MapPost("/api/claude/test", ClaudeTestEndpoint.TestAsync).WithTags("Claude");
        return endpoints;
    }

    /// <summary>Queues one item per target without an open item; 400 when empty, invalid or over the limit; 404 for an unknown run.</summary>
    private static async Task<Results<Ok<CreateExternalReviewsResponse>, ValidationProblem, NotFound>> CreateAsync(
        CreateExternalReviewsRequest request, ExternalReviewService reviews, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var max = ExternalReviewService.MaxTargets;
        if (request.SuggestionIds is not { Length: > 0 } && request.Groups is not { Length: > 0 } && request.RunId is null)
        {
            errors["request"] = ["Pass suggestionIds, groups or runId."];
        }

        if (request.SuggestionIds is { Length: > ExternalReviewService.MaxTargets })
        {
            errors["suggestionIds"] = [$"At most {max} ids."];
        }

        var groups = new List<GroupRef>();
        if (request.Groups is { Length: > ExternalReviewService.MaxTargets })
        {
            errors["groups"] = [$"At most {max} groups."];
        }
        else
        {
            foreach (var g in request.Groups ?? [])
            {
                var sender = g?.SenderAddress?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(sender) || sender.Length > AnalysisPreviewEndpoint.MaxSenderAddressLength
                    || string.IsNullOrEmpty(g!.GroupKey) || g.GroupKey.Length > ReviewEndpoints.MaxGroupKeyLength)
                {
                    errors["groups"] = [$"Each group needs a sender address (at most {AnalysisPreviewEndpoint.MaxSenderAddressLength} characters) and a group key (at most {ReviewEndpoints.MaxGroupKeyLength})."];
                    break;
                }

                groups.Add(new GroupRef(sender, g.GroupKey));
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        return await reviews.CreateAsync(request.SuggestionIds ?? [], [.. groups], request.RunId, ct) switch
        {
            (CreateExternalReviewsResult.Ok, { } response) => TypedResults.Ok(response),
            (CreateExternalReviewsResult.RunNotFound, _) => TypedResults.NotFound(),
            _ => TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["request"] = [request.RunId is null
                    ? $"At most {max} suggestions and groups in total."
                    : $"At most {max} suggestions and groups in total, counting the run's."],
            }),
        };
    }

    /// <summary>Items in <c>status</c> (any when omitted), newest first.</summary>
    private static async Task<Results<Ok<PagedDto<ExternalReviewDto>>, ValidationProblem>> ListAsync(
        ExternalReviewQuery query, CancellationToken ct, string? status = null, int? page = null, int? pageSize = null)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ExternalReviewQuery.TryParseStatus(status, out var parsed))
        {
            errors["status"] = ["Must be queued, running, reviewed, unavailable or cancelled."];
        }

        if (page is < 1 or > SenderQuery.MaxPage)
        {
            errors["page"] = [$"Must be between 1 and {SenderQuery.MaxPage}."];
        }

        if (pageSize is < 1 or > ExternalReviewQuery.MaxPageSize)
        {
            errors["pageSize"] = [$"Must be between 1 and {ExternalReviewQuery.MaxPageSize}."];
        }

        return errors.Count > 0
            ? TypedResults.ValidationProblem(errors)
            : TypedResults.Ok(await query.ListAsync(parsed, page ?? 1, pageSize ?? ExternalReviewQuery.DefaultPageSize, ct));
    }

    private static async Task<Results<Ok<ExternalReviewDto>, ProblemHttpResult>> ToResultAsync(
        Task<(ExternalReviewResult Result, ExternalReviewDto? Item)> action) =>
        await action switch
        {
            (ExternalReviewResult.Ok, { } item) => TypedResults.Ok(item),
            (ExternalReviewResult.NotFound, _) => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Review item not found"),
            (ExternalReviewResult.AlreadyDecided, _) => Conflict("Already decided", "The suggestions are no longer pending."),
            (ExternalReviewResult.NeedsHuman, _) => Conflict("Needs a human", "Claude gave no verdict to accept; decide on the review page."),
            (ExternalReviewResult.NotApplicable, _) => Conflict(
                "Not applicable", "No pending suggestion can take Claude's outcome (protected mail is never marked to-be-deleted, or the suggestions changed since the review); decide on the review page."),
            (ExternalReviewResult.InvalidVerdict, _) => Conflict("Invalid verdict", "Claude's label is not a valid label path; edit on the review page."),
            (_, var item) => Conflict("Wrong status", $"Not allowed while the item is {item?.Status ?? "in this status"}."),
        };

    private static ProblemHttpResult Conflict(string title, string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);
}

/// <param name="Mode">The reviewer mode, as the settings API names it.</param>
/// <param name="Error">What is wrong, as text the UI shows verbatim; null when <paramref name="Ok"/>.</param>
public sealed record ClaudeTestResultDto(
    bool Ok, string Mode, string? CliVersion, bool TokenSet, bool McpReachable, long ElapsedMs, string? Error);

/// <summary>
/// <c>POST /api/claude/test</c>: headless mode checks <c>claude --version</c>, that the OAuth token is set (never its
/// value) and that <c>/mcp</c> answers <c>tools/list</c> with the bearer token, as the CLI will call it; desktop mode
/// checks <c>/mcp</c> only. Never throws for a failed check.
/// </summary>
public static class ClaudeTestEndpoint
{
    public const string McpClientName = "claude-mcp-self";
    public static readonly TimeSpan McpTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<Ok<ClaudeTestResultDto>> TestAsync(
        ISettingsStore settings,
        IClaudeCliRunner runner,
        IOptions<SettingsEnvOptions> env,
        McpTokenService tokens,
        IServer server,
        IHttpClientFactory http,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var mode = (await settings.GetAsync(ct)).ClaudeReviewerMode;
        var modeName = JsonNamingPolicy.SnakeCaseLower.ConvertName(mode.ToString());
        var tokenSet = env.Value.ClaudeCodeOAuthTokenSet;
        if (mode == ClaudeReviewerMode.Off)
        {
            return Result(false, null, false, "Claude review is off; choose a mode first.");
        }

        var errors = new List<string>();
        string? version = null;
        if (mode == ClaudeReviewerMode.HeadlessClaudeCode)
        {
            var v = await runner.VersionAsync(ct);
            version = v.Version;
            if (v.Error is not null)
            {
                errors.Add(v.Error);
            }

            if (!tokenSet)
            {
                errors.Add(ClaudeReviewJob.TokenMissingMessage);
            }
        }

        var mcpError = await ProbeMcpAsync(ClaudeExtensions.McpSelfUrl(server), tokens, http, loggers, ct);
        if (mcpError is not null)
        {
            errors.Add(mcpError);
        }

        return Result(errors.Count == 0, version, mcpError is null, errors.Count == 0 ? null : string.Join(' ', errors));

        Ok<ClaudeTestResultDto> Result(bool ok, string? cliVersion, bool mcpReachable, string? error) => TypedResults.Ok(
            new ClaudeTestResultDto(ok, modeName, cliVersion, tokenSet, mcpReachable, (long)time.GetElapsedTime(started).TotalMilliseconds, error));
    }

    /// <summary>Null when <c>/mcp</c> lists the review tools for the current token; otherwise why not.</summary>
    private static async Task<string?> ProbeMcpAsync(
        string url, McpTokenService tokens, IHttpClientFactory http, ILoggerFactory loggers, CancellationToken ct)
    {
        try
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(url),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {await tokens.GetOrCreateAsync(ct)}" },
                },
                http.CreateClient(McpClientName),
                ownsHttpClient: true);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return tools.Any(t => ClaudeReviewJob.AllowedTools.Contains($"mcp__{McpExtensions.ServerName}__{t.Name}"))
                ? null
                : $"The MCP endpoint {url} answered without the review tools.";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            loggers.CreateLogger(typeof(ClaudeTestEndpoint)).LogInformation("MCP self-check failed ({Error})", ex.GetType().Name);
            return $"The MCP endpoint {url} is not reachable: {ClaudeCliOutput.Sanitise(ex.Message) ?? ex.GetType().Name}";
        }
    }
}
