using System.Collections.Concurrent;
using GmailOrganiser.Claude;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>
/// The Claude Code CLI for tests (CI has none): <see cref="Version"/> answers <c>--version</c>, and each run is
/// scripted by <see cref="Run"/> with the request and its 1-based call number. <see cref="SubmitAllAsync"/> does what
/// Claude does in a run, through <see cref="McpTestClient"/> with the request's token.
/// </summary>
public sealed class FakeClaudeCliRunner : IClaudeCliRunner
{
    public const string FakeVersion = "0.0.0-fake (Claude Code)";
    public const string FakeModel = "synthetic-claude-model";

    private int runCalls;
    private int versionCalls;

    public ClaudeVersionResult Version { get; set; } = new(FakeVersion, null, null);

    /// <summary>The run script; by default a successful run that submits nothing.</summary>
    public Func<ClaudeRunRequest, int, CancellationToken, Task<ClaudeRunResult>> Run { get; set; } =
        (_, _, _) => Task.FromResult(Success());

    public ConcurrentQueue<ClaudeRunRequest> Requests { get; } = new();

    public int RunCalls => runCalls;

    public int VersionCalls => versionCalls;

    public Task<ClaudeVersionResult> VersionAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref versionCalls);
        return Task.FromResult(Version);
    }

    public Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return Run(request, Interlocked.Increment(ref runCalls), ct);
    }

    public static ClaudeRunResult Success(string? model = FakeModel) =>
        new(true, "Synthetic summary.", 7, null, null, TimeSpan.FromSeconds(1), model);

    /// <summary>
    /// Lists the pending items over MCP with the request's token and submits <c>agree</c> for each, as Claude would;
    /// returns how many it submitted.
    /// </summary>
    public static async Task<int> SubmitAllAsync(WebApplicationFactory<Program> host, ClaudeRunRequest request, CancellationToken ct)
    {
        await using var client = await McpTestClient.ConnectAsync(host, ct, request.Token);
        var listed = McpTestClient.Structured(await McpTestClient.CallAsync(client, "list_pending_reviews", ct));
        var ids = listed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        foreach (var id in ids)
        {
            McpTestClient.Structured(await McpTestClient.CallAsync(client, "submit_review", ct, new Dictionary<string, object?>
            {
                ["id"] = id,
                ["verdict"] = "agree",
                ["reasoning"] = "Synthetic reasoning.",
            })).GetProperty("ok").GetBoolean().ShouldBeTrue();
        }

        return ids.Count;
    }
}
