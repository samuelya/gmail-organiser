using Microsoft.Extensions.AI;

namespace GmailOrganiser.Llm;

/// <summary>Tokens and time spent on chat calls, summed per run; <see cref="NearContextLimit"/> counts calls near <c>num_ctx</c>.</summary>
public readonly record struct LlmUsage(long PromptTokens, long CompletionTokens, long Milliseconds, int NearContextLimit)
{
    public static LlmUsage operator +(LlmUsage a, LlmUsage b) => new(
        a.PromptTokens + b.PromptTokens,
        a.CompletionTokens + b.CompletionTokens,
        a.Milliseconds + b.Milliseconds,
        a.NearContextLimit + b.NearContextLimit);
}

/// <summary>
/// Times one chat call and reads its <see cref="ChatResponse.Usage"/> (null from the fake or a failing model: 0), logs
/// model, tokens, ms and group size (never content) and warns when the prompt fills ≥ <see cref="NearLimitRatio"/> of
/// <c>num_ctx</c>, where Ollama truncates silently (#353).
/// </summary>
public sealed partial class LlmCallMeter(TimeProvider time, ILogger logger)
{
    /// <summary>
    /// The <see cref="ChatOptions.AdditionalProperties"/> key OllamaSharp maps to the request's <c>options.num_ctx</c>
    /// (<c>OllamaOption.NumCtx.Name</c>); other clients ignore it.
    /// </summary>
    public const string NumCtxKey = "num_ctx";

    public const double NearLimitRatio = 0.9;

    public async Task<(ChatResponse Response, LlmUsage Usage)> GetResponseAsync(
        IChatClient chat, IEnumerable<ChatMessage> messages, ChatOptions options, string? model, int numCtx, int groupSize,
        CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var response = await chat.GetResponseAsync(messages, options, ct);
        var ms = (long)time.GetElapsedTime(started).TotalMilliseconds;
        var prompt = Math.Max(0, response.Usage?.InputTokenCount ?? 0);
        var completion = Math.Max(0, response.Usage?.OutputTokenCount ?? 0);
        LogCall(logger, model, prompt, completion, ms, groupSize);
        var near = numCtx > 0 && prompt >= NearLimitRatio * numCtx;
        if (near)
        {
            LogNearLimit(logger, model, prompt, numCtx);
        }

        return (response, new LlmUsage(prompt, completion, ms, near ? 1 : 0));
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "LLM call: model {Model}, {PromptTokens} prompt tokens, {CompletionTokens} completion tokens, {Milliseconds} ms, group size {GroupSize}")]
    private static partial void LogCall(ILogger logger, string? model, long promptTokens, long completionTokens, long milliseconds, int groupSize);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "LLM prompt near the context limit, likely truncated: model {Model}, {PromptTokens} prompt tokens, num_ctx {NumCtx}")]
    private static partial void LogNearLimit(ILogger logger, string? model, long promptTokens, int numCtx);
}
