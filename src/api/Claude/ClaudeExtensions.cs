using GmailOrganiser.Jobs;
using GmailOrganiser.Mcp;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GmailOrganiser.Claude;

public static class ClaudeExtensions
{
    /// <summary>
    /// Registers the Claude review queue, the headless review job with its starter and CLI runner, and the SignalR item
    /// notifier. Needs <c>AddReview</c>, <c>AddJobs</c> and <c>AddMcpServer</c>.
    /// </summary>
    public static IServiceCollection AddClaude(this IServiceCollection services)
    {
        services.AddOptions<ClaudeCliOptions>().BindConfiguration(ClaudeCliOptions.SectionName);
        services.AddScoped<ExternalReviewQuery>();
        services.AddScoped<ExternalReviewService>();
        services.AddScoped<IClaudeReviewStarter, HeadlessClaudeReviewStarter>();
        services.TryAddSingleton<IClaudeCliRunner, ProcessClaudeCliRunner>();
        services.TryAddSingleton<IExternalReviewNotifier, SignalRExternalReviewNotifier>();
        services.AddKeyedScoped<IJobHandler, ClaudeReviewJob>(ClaudeReviewJob.JobType);
        services.AddHttpClient(ClaudeTestEndpoint.McpClientName, c => c.Timeout = ClaudeTestEndpoint.McpTimeout);
        return services;
    }

    /// <summary>
    /// The api's own <c>/mcp</c> as the CLI in the same container reaches it: <c>localhost</c> and the port Kestrel
    /// listens on (an <c>http</c> address preferred), so no config is needed; plain <c>http://localhost/mcp</c> when the
    /// server reports no address (the test host).
    /// </summary>
    public static string McpSelfUrl(IServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var addresses = (server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
            .Select(a => BindingAddress.Parse(a))
            .Where(a => !a.IsUnixPipe)
            .OrderBy(a => a.Scheme == Uri.UriSchemeHttp ? 0 : 1)
            .ToList();
        return addresses.FirstOrDefault() is { } address
            ? $"{address.Scheme}://localhost:{address.Port}{McpExtensions.Path}"
            : $"{Uri.UriSchemeHttp}://localhost{McpExtensions.Path}";
    }
}
