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
        group.MapGet("/models", GetModelsAsync);
        group.MapPost("/test-model", TestModelAsync);
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

    private static async Task<Ok<ClaudeApiModelsDto>> GetModelsAsync(IClaudeApiCatalog catalog, CancellationToken ct) =>
        TypedResults.Ok(await catalog.ListModelsAsync(ct));

    // No saved key throws LlmNotConfiguredException, which UseLlmNotConfiguredProblem maps to 409.
    private static async Task<Results<Ok<TestModelResultDto>, ValidationProblem>> TestModelAsync(
        TestClaudeApiModelRequest request, LlmModelTester tester, CancellationToken ct)
    {
        if (!LlmEndpoints.IsValidModel(request.Model))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["model"] = [LlmEndpoints.ModelError] });
        }

        return TypedResults.Ok(await tester.TestClaudeApiAsync(request.Model!.Trim(), ct));
    }
}
