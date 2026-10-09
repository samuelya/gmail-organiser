using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis;

/// <summary>The run's metered chat calls: each client carries the context limit and error wording of the server it calls.</summary>
public sealed partial class AnalysisRunJob
{
    /// <summary>
    /// A chat client with the meter's <c>num_ctx</c> limit and its server's error text (#488): Ollama (the chat model with the
    /// Ollama provider, and always the triage model) keeps <see cref="AppSettings.LlmNumCtx"/> and <see cref="OllamaErrors"/>;
    /// the Claude API counts no near-limit calls and keeps its client's own key-free message.
    /// </summary>
    private sealed record MeteredChat(IChatClient Client, int NumCtx, Func<Exception, string> Describe);

    private MeteredChat OllamaChat(IChatClient client, AppSettings settings) =>
        new(client, settings.LlmNumCtx, ex => OllamaErrors.Describe(ex, OllamaHttp.Parse(settings.OllamaBaseUrl), llmOptions.Value.ModelTimeout));

    private MeteredChat ActiveChat(IChatClient client, ChatConfiguration chat) =>
        chat.OnOllama ? OllamaChat(client, chat.Settings) : new(client, chat.Settings.MeterNumCtx, DescribeClaudeApiError);

    // The Claude API client maps HTTP errors and its own timeout; any other cancellation still says which call failed.
    private static string DescribeClaudeApiError(Exception ex) => ex is TaskCanceledException
        ? "The Claude API call was cancelled or timed out before it answered."
        : ex.Message;

    private Task<(string Text, LlmUsage Usage)> ChatAsync(
        RunContext context, MeteredChat chat, string? model, IList<ChatMessage> messages, int groupSize, CancellationToken ct) =>
        ChatAsync(chat, model, messages, AnalysisPromptBuilder.CreateOptions(context.Settings.LlmNumCtx), groupSize, ct);

    /// <summary>One metered chat call; an unreachable or timed-out model becomes <see cref="AnalysisModelUnavailableException"/>.</summary>
    private async Task<(string Text, LlmUsage Usage)> ChatAsync(
        MeteredChat chat, string? model, IList<ChatMessage> messages, ChatOptions options, int groupSize, CancellationToken ct)
    {
        try
        {
            var (response, usage) = await _meter.GetResponseAsync(chat.Client, messages, options, model, chat.NumCtx, groupSize, ct);
            return (response.Text, usage);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OllamaSharp.Models.Exceptions.OllamaException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new AnalysisModelUnavailableException(chat.Describe(ex), ex);
        }
    }
}
