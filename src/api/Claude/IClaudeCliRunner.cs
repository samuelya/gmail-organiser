namespace GmailOrganiser.Claude;

/// <summary>Runs the Claude Code CLI as a child process (DESIGN §6.7); never throws for a failed run.</summary>
public interface IClaudeCliRunner
{
    /// <summary><c>claude --version</c> with a <see cref="ProcessClaudeCliRunner.VersionTimeout"/> timeout.</summary>
    Task<ClaudeVersionResult> VersionAsync(CancellationToken ct);

    /// <summary>One headless run (<c>claude -p</c>) against the api's own MCP endpoint.</summary>
    Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken ct);
}

/// <param name="Token">The <c>/mcp</c> bearer token, written to the run's MCP config file only.</param>
/// <param name="Model">The <c>--model</c> value; none passed when null (the subscription default).</param>
public sealed record ClaudeRunRequest(
    string Prompt,
    string McpEndpointUrl,
    string Token,
    IReadOnlyList<string> AllowedTools,
    int MaxTurns,
    string? Model,
    TimeSpan Timeout);

/// <param name="ErrorKind">One of <see cref="ClaudeErrorKinds"/> when not <paramref name="Ok"/>.</param>
/// <param name="Error">One sanitised line (no tokens) for the user; shown verbatim.</param>
/// <param name="Model">The model the CLI reports it used, when it does.</param>
public sealed record ClaudeRunResult(
    bool Ok,
    string? ResultText,
    int? NumTurns,
    string? ErrorKind,
    string? Error,
    TimeSpan Elapsed,
    string? Model = null)
{
    public static ClaudeRunResult Fail(string kind, string error, TimeSpan elapsed = default) =>
        new(false, null, null, kind, error, elapsed);
}

/// <param name="Version">The first line of <c>claude --version</c>; null when it failed.</param>
public sealed record ClaudeVersionResult(string? Version, string? ErrorKind, string? Error);

/// <summary>What went wrong in a run; the UI shows the error text verbatim.</summary>
public static class ClaudeErrorKinds
{
    public const string CliMissing = "cli_missing";
    public const string TokenMissing = "token_missing";
    public const string Auth = "auth";
    public const string RateLimit = "rate_limit";
    public const string Timeout = "timeout";
    public const string Failed = "failed";
}

/// <summary>Where the CLI is, bound from the <c>Claude</c> section.</summary>
public sealed class ClaudeCliOptions
{
    public const string SectionName = "Claude";

    /// <summary>The executable: a name looked up on <c>PATH</c>, or a path.</summary>
    public string CliPath { get; set; } = "claude";
}
