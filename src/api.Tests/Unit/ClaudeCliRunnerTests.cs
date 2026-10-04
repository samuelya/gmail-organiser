using System.Text.Json;
using GmailOrganiser.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit;

/// <summary>
/// The CLI argument builder, the output parser with recorded-shape samples (synthetic), error sanitising, and
/// <see cref="ProcessClaudeCliRunner"/> against a shell script standing in for the CLI (no real CLI in CI).
/// </summary>
public sealed class ClaudeCliRunnerTests : IDisposable
{
    private const string McpToken = "SyntheticMcpToken_abcdefghijklmnopqrstuvwxyz0123456789";

    private const string SuccessJson = """
        {"type":"result","subtype":"success","is_error":false,"duration_ms":51234,"num_turns":12,
         "result":"Agreed with 2, changed 1, left 0 to the user.","session_id":"00000000-0000-0000-0000-000000000000",
         "total_cost_usd":0.1,"usage":{"input_tokens":10},"permission_denials":[],"future_field":{"x":1},
         "modelUsage":{"synthetic-small":{"outputTokens":40},"synthetic-large":{"outputTokens":900}}}
        """;

    private readonly string dir = Directory.CreateTempSubdirectory("gmo-claude-test-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaudeRunRequest Request(string? model = null, TimeSpan? timeout = null) => new(
        "Review the pending items.",
        "http://localhost:8080/mcp",
        McpToken,
        ClaudeReviewJob.AllowedTools,
        80,
        model,
        timeout ?? TimeSpan.FromSeconds(30));

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void Arguments_put_the_prompt_after_p_and_each_value_separately()
    {
        var args = ProcessClaudeCliRunner.BuildArguments(Request(), "/tmp/x/mcp.json");

        args.ShouldBe([
            "-p", "Review the pending items.",
            "--output-format", "json",
            "--mcp-config", "/tmp/x/mcp.json",
            "--strict-mcp-config",
            "--allowedTools", "mcp__gmail-organiser__list_pending_reviews,mcp__gmail-organiser__get_review_item,mcp__gmail-organiser__get_label_tree,mcp__gmail-organiser__submit_review,mcp__gmail-organiser__get_filters,mcp__gmail-organiser__get_label_plan,mcp__gmail-organiser__submit_taxonomy_feedback",
            "--max-turns", "80",
        ]);
        args.ShouldNotContain(a => a.Contains(McpToken));
        ProcessClaudeCliRunner.BuildArguments(Request(" synthetic-model "), "c").TakeLast(2).ShouldBe(["--model", "synthetic-model"]);
        Should.Throw<ArgumentException>(() => ProcessClaudeCliRunner.BuildArguments(Request("--dangerous"), "c"));
    }

    [Fact]
    public void Mcp_config_points_at_the_server_with_the_bearer_token()
    {
        using var doc = JsonDocument.Parse(ProcessClaudeCliRunner.McpConfigJson(Request()));
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("gmail-organiser");
        server.GetProperty("type").GetString().ShouldBe("http");
        server.GetProperty("url").GetString().ShouldBe("http://localhost:8080/mcp");
        server.GetProperty("headers").GetProperty("Authorization").GetString().ShouldBe($"Bearer {McpToken}");
    }

    [Fact]
    public void Parser_reads_the_result_and_ignores_unknown_fields()
    {
        var output = ClaudeCliOutput.Parse(SuccessJson).ShouldNotBeNull();

        output.ShouldBe(new ClaudeCliOutput(false, "success", "Agreed with 2, changed 1, left 0 to the user.", 12, "synthetic-large"));
    }

    [Theory]
    [InlineData("""{"type":"result","subtype":"error_max_turns","is_error":false,"num_turns":80}""", true, "error_max_turns", null)]
    [InlineData("""{"type":"result","subtype":"success","is_error":true,"result":"Invalid API key · Please run /login"}""", true, "success", "Invalid API key · Please run /login")]
    [InlineData("warning: something synthetic\n{\"type\":\"result\",\"result\":\"done\",\"model\":\"synthetic-model\"}", false, null, "done")]
    [InlineData("""[{"type":"system","subtype":"init"},{"type":"result","subtype":"success","is_error":false,"result":"done"}]""", false, "success", "done")]
    public void Parser_is_tolerant_of_errors_log_lines_and_message_arrays(string stdout, bool isError, string? subtype, string? result)
    {
        var output = ClaudeCliOutput.Parse(stdout).ShouldNotBeNull();

        (output.IsError, output.Subtype, output.Result).ShouldBe((isError, subtype, result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"type":"assistant"}""")]
    [InlineData("[1,2]")]
    public void Parser_returns_null_without_a_result_message(string stdout) => ClaudeCliOutput.Parse(stdout).ShouldBeNull();

    [Theory]
    [InlineData("API Error: 401 {\"error\":\"OAuth token has expired\"}", ClaudeErrorKinds.Auth)]
    [InlineData("Invalid API key · Please run /login", ClaudeErrorKinds.Auth)]
    [InlineData("Claude AI usage limit reached|1760000000", ClaudeErrorKinds.RateLimit)]
    [InlineData("API Error: 429 Too Many Requests", ClaudeErrorKinds.RateLimit)]
    [InlineData("Something else broke", ClaudeErrorKinds.Failed)]
    public void Errors_are_classified(string text, string kind) => ClaudeCliOutput.Classify(text).ShouldBe(kind);

    [Fact]
    public void Sanitise_masks_tokens_and_query_strings_and_keeps_one_short_line()
    {
        var text = $"Failed to connect\n  Authorization: Bearer abc.def\n{McpToken} sk-ant-oat01-synthetic https://example.com/x?token=secret-value";

        var line = ClaudeCliOutput.Sanitise(text, "abc.def").ShouldNotBeNull();

        line.ShouldBe("Failed to connect Authorization: Bearer *** *** *** https://example.com/x?***");
        ClaudeCliOutput.Sanitise(string.Join(' ', Enumerable.Repeat("word", 200)))!.Length.ShouldBe(ClaudeCliOutput.MaxErrorLength);
        ClaudeCliOutput.Sanitise("  \n ").ShouldBeNull();
    }

    [Fact]
    public async Task Missing_cli_is_cli_missing_with_the_rebuild_hint()
    {
        var runner = Runner(Path.Combine(dir, "no-such-claude"));

        var version = await runner.VersionAsync(Ct);
        var run = await runner.RunAsync(Request(), Ct);

        (version.Version, version.ErrorKind).ShouldBe((null, ClaudeErrorKinds.CliMissing));
        (run.Ok, run.ErrorKind).ShouldBe((false, ClaudeErrorKinds.CliMissing));
        run.Error.ShouldNotBeNull().ShouldContain("WITH_CLAUDE=true");
    }

    [Fact]
    public async Task Script_cli_gets_the_arguments_and_config_and_its_result_is_parsed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var record = Path.Combine(dir, "record");
        var runner = Runner(Script($"""
            if [ "$1" = "--version" ]; then echo "9.9.9 (Claude Code)"; exit 0; fi
            printf '%s\n' "$@" > "{record}.args"
            cat "$6" > "{record}.config"
            pwd > "{record}.cwd"
            cat <<'JSON'
            {SuccessJson.ReplaceLineEndings(" ")}
            JSON
            """));

        (await runner.VersionAsync(Ct)).Version.ShouldBe("9.9.9 (Claude Code)");
        var result = await runner.RunAsync(Request(), Ct);

        result.ShouldSatisfyAllConditions(
            r => (r.Ok, r.NumTurns, r.Model, r.ErrorKind).ShouldBe((true, 12, "synthetic-large", null)),
            r => r.ResultText.ShouldBe("Agreed with 2, changed 1, left 0 to the user."));
        var args = await File.ReadAllLinesAsync(record + ".args", Ct);
        args.ShouldBe(ProcessClaudeCliRunner.BuildArguments(Request(), args[5]));
        Path.GetFileName(args[5]).ShouldBe("mcp.json");
        (await File.ReadAllTextAsync(record + ".cwd", Ct)).Trim().ShouldEndWith(Path.GetFileName(Path.GetDirectoryName(args[5]))!);
        (await File.ReadAllTextAsync(record + ".config", Ct)).ShouldBe(ProcessClaudeCliRunner.McpConfigJson(Request()));
        Directory.Exists(Path.GetDirectoryName(args[5])).ShouldBeFalse();
    }

    [Theory]
    [InlineData("echo 'API Error: 401 Bearer abc OAuth token has expired' >&2; exit 1", ClaudeErrorKinds.Auth, "API Error: 401 Bearer *** OAuth token has expired")]
    [InlineData("""echo '{"type":"result","is_error":true,"result":"Claude AI usage limit reached"}'; exit 1""", ClaudeErrorKinds.RateLimit, "Claude AI usage limit reached")]
    [InlineData($"echo 'mcp failed with {McpToken}' >&2; exit 2", ClaudeErrorKinds.Failed, "mcp failed with ***")]
    public async Task Failed_run_is_classified_with_one_sanitised_line(string body, string kind, string error)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await Runner(Script(body)).RunAsync(Request(), Ct);

        (result.Ok, result.ErrorKind, result.Error).ShouldBe((false, kind, error));
    }

    [Fact]
    public async Task Run_over_the_timeout_is_killed_and_reported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await Runner(Script("sleep 30")).RunAsync(Request(timeout: TimeSpan.FromMilliseconds(300)), Ct);

        (result.Ok, result.ErrorKind).ShouldBe((false, ClaudeErrorKinds.Timeout));
        result.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    private static ProcessClaudeCliRunner Runner(string cliPath) => new(
        Options.Create(new ClaudeCliOptions { CliPath = cliPath }), TimeProvider.System, NullLogger<ProcessClaudeCliRunner>.Instance);

    private string Script(string body)
    {
        var path = Path.Combine(dir, $"claude-{Guid.NewGuid():N}");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
