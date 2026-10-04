using Microsoft.Extensions.Options;

namespace GmailOrganiser.Llm.Fake;

/// <summary>Logs once at start-up that <c>LLM_FAKE=true</c> replaces every model.</summary>
public sealed class LlmFakeNotice(IOptions<LlmOptions> options, ILogger<LlmFakeNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.UseFake)
        {
            logger.LogWarning("LLM fake is on; no model is used.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
