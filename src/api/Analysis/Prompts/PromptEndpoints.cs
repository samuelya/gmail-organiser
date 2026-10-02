namespace GmailOrganiser.Analysis.Prompts;

public static class PromptEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisPromptEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/analysis/prompt").WithTags("Analysis");
        group.MapGet("/default", () => TypedResults.Ok(new PromptTemplateDto(PromptTemplate.BuiltIn.Version, PromptTemplate.BuiltIn.Text)));
        return endpoints;
    }
}
