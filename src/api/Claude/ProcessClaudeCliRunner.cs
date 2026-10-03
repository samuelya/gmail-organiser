using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GmailOrganiser.Mcp;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Claude;

/// <summary>
/// The Claude Code CLI as a child process. It inherits the api's environment, so <c>CLAUDE_CODE_OAUTH_TOKEN</c> reaches
/// it without the app ever reading it. Each run gets a fresh temp directory (0700) as its working directory, holding the
/// MCP config (0600, the only place the <c>/mcp</c> token is written), deleted in <c>finally</c>. Arguments are added one
/// by one, never through a shell. The process tree is killed on timeout or cancellation. stdout is parsed with
/// <see cref="ClaudeCliOutput"/>; stderr is reduced to one sanitised line and never logged.
/// </summary>
public sealed class ProcessClaudeCliRunner(IOptions<ClaudeCliOptions> options, TimeProvider time, ILogger<ProcessClaudeCliRunner> logger)
    : IClaudeCliRunner
{
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public const string CliMissingMessage =
        "The Claude Code CLI is not installed in the api image. Rebuild it with WITH_CLAUDE=true (docker compose build --build-arg WITH_CLAUDE=true api), or install the CLI for an IDE run.";

    private static readonly JsonSerializerOptions ConfigJson = new() { WriteIndented = false };

    public async Task<ClaudeVersionResult> VersionAsync(CancellationToken ct)
    {
        var exec = await ExecuteAsync(["--version"], null, VersionTimeout, ct);
        if (exec.Missing)
        {
            return new(null, ClaudeErrorKinds.CliMissing, CliMissingMessage);
        }

        if (exec.TimedOut)
        {
            return new(null, ClaudeErrorKinds.Timeout, $"claude --version did not answer within {VersionTimeout.TotalSeconds:0} seconds.");
        }

        var version = exec.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return exec.ExitCode == 0 && version is not null
            ? new(ClaudeCliOutput.Sanitise(version), null, null)
            : new(null, ClaudeErrorKinds.Failed, ClaudeCliOutput.Sanitise(exec.Stderr) ?? $"claude --version exited with code {exec.ExitCode}.");
    }

    public async Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = time.GetTimestamp();
        if (request.Model is { } model && !IsValidModel(model))
        {
            return ClaudeRunResult.Fail(ClaudeErrorKinds.Failed, "The Claude model setting is not valid; change it in Settings.");
        }

        DirectoryInfo? dir = null;
        try
        {
            dir = Directory.CreateTempSubdirectory("gmo-claude-");
            var configPath = Path.Combine(dir.FullName, "mcp.json");
            await WriteConfigAsync(configPath, request, ct);
            var exec = await ExecuteAsync(BuildArguments(request, configPath), dir.FullName, request.Timeout, ct);
            return ToResult(exec, request, time.GetElapsedTime(started));
        }
        finally
        {
            try
            {
                dir?.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The Claude run's temp directory could not be deleted ({Error})", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// <c>-p &lt;prompt&gt; --output-format json --mcp-config &lt;file&gt; --strict-mcp-config --allowedTools &lt;a,b,…&gt;
    /// --max-turns &lt;n&gt; [--model &lt;m&gt;]</c>; the prompt follows <c>-p</c> so no variadic option can take it.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(ClaudeRunRequest request, string mcpConfigPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<string> args =
        [
            "-p", request.Prompt,
            "--output-format", "json",
            "--mcp-config", mcpConfigPath,
            "--strict-mcp-config",
            "--allowedTools", string.Join(',', request.AllowedTools),
            "--max-turns", request.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            if (!IsValidModel(request.Model))
            {
                throw new ArgumentException("The model must not start with '-'.", nameof(request));
            }

            args.AddRange(["--model", request.Model.Trim()]);
        }

        return args;
    }

    /// <summary>A model value can't be mistaken for an option.</summary>
    public static bool IsValidModel(string model) => !model.TrimStart().StartsWith('-');

    /// <summary>The <c>--mcp-config</c> JSON: the api's own server over HTTP with the bearer token.</summary>
    public static string McpConfigJson(ClaudeRunRequest request) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["mcpServers"] = new Dictionary<string, object>
        {
            [McpExtensions.ServerName] = new Dictionary<string, object>
            {
                ["type"] = "http",
                ["url"] = request.McpEndpointUrl,
                ["headers"] = new Dictionary<string, string> { ["Authorization"] = $"Bearer {request.Token}" },
            },
        },
    }, ConfigJson);

    private static async Task WriteConfigAsync(string path, ClaudeRunRequest request, CancellationToken ct)
    {
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, fileOptions);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(McpConfigJson(request)), ct);
    }

    private static ClaudeRunResult ToResult(Exec exec, ClaudeRunRequest request, TimeSpan elapsed)
    {
        if (exec.Missing)
        {
            return ClaudeRunResult.Fail(ClaudeErrorKinds.CliMissing, CliMissingMessage, elapsed);
        }

        if (exec.TimedOut)
        {
            return ClaudeRunResult.Fail(
                ClaudeErrorKinds.Timeout, $"Claude did not finish within {request.Timeout.TotalSeconds:0} seconds; the run was stopped.", elapsed);
        }

        var output = ClaudeCliOutput.Parse(exec.Stdout);
        if (exec.ExitCode == 0 && output is { IsError: false })
        {
            return new(true, output.Result, output.NumTurns, null, null, elapsed, output.Model);
        }

        // The CLI reports most failures in the result text (is_error); stderr has the rest.
        var raw = output?.Subtype == "error_max_turns"
            ? $"Claude stopped after the maximum of {request.MaxTurns} turns."
            : string.Join(' ', new[] { output?.Result, exec.Stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var error = ClaudeCliOutput.Sanitise(raw, request.Token)
            ?? (output is null ? $"The Claude Code CLI exited with code {exec.ExitCode} and no result." : "Claude reported an error without a message.");
        return new(false, null, output?.NumTurns, ClaudeCliOutput.Classify(raw), error, elapsed, output?.Model);
    }

    private async Task<Exec> ExecuteAsync(IEnumerable<string> arguments, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.Value.CliPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The Claude Code CLI did not start.");
        }
        catch (Win32Exception)
        {
            return new(null, "", "", TimedOut: false, Missing: true);
        }

        using (process)
        using (var timer = new CancellationTokenSource(timeout, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token))
        {
            try
            {
                // No input: -p must not wait for piped stdin.
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(linked.Token);
                var errors = process.StandardError.ReadToEndAsync(linked.Token);
                await process.WaitForExitAsync(linked.Token);
                return new(process.ExitCode, await output, await errors, TimedOut: false, Missing: false);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return new(null, "", "", TimedOut: true, Missing: false);
            }
            finally
            {
                Kill(process);
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited meanwhile.
        }
    }

    private sealed record Exec(int? ExitCode, string Stdout, string Stderr, bool TimedOut, bool Missing);
}
