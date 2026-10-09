using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Llm.ClaudeApi;

/// <summary>The write-only Claude API key; the key is never returned (the settings read has only whether it is set and a hint).</summary>
public sealed record SetClaudeApiKeyRequest(string? ApiKey);

public static class ClaudeApiEndpoints
{
    public static IEndpointRouteBuilder MapClaudeApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/llm/claude-api").WithTags("Llm");
        group.MapPut("/key", SetKeyAsync);
        group.MapDelete("/key", ClearKeyAsync);
        return endpoints;
    }

    private static async Task<Results<NoContent, ValidationProblem>> SetKeyAsync(
        SetClaudeApiKeyRequest request, ClaudeApiKeyService keys, CancellationToken ct)
    {
        var (key, errors) = ClaudeApiValidation.Validate(request);
        if (key is null)
        {
            return TypedResults.ValidationProblem(errors!);
        }

        await keys.SetAsync(key, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> ClearKeyAsync(ClaudeApiKeyService keys, CancellationToken ct)
    {
        await keys.ClearAsync(ct);
        return TypedResults.NoContent();
    }
}
