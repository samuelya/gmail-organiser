using GmailOrganiser.Llm.ClaudeApi;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Llm;

public static class LlmEndpoints
{
    /// <summary>
    /// Registers the Ollama and Claude API catalogs, the named Ollama and Claude API clients and the client factory; with <see cref="LlmOptions.UseFake"/> the catalog is
    /// <see cref="FakeOllamaCatalog"/> and <see cref="FakeClaudeApiCatalog"/> (chosen at resolution time) and the factory hands out the <c>Llm/Fake</c> clients.
    /// </summary>
    public static IServiceCollection AddLlm(this IServiceCollection services)
    {
        services.AddOptions<LlmOptions>()
            .BindConfiguration(LlmOptions.SectionName)
            .Configure<IConfiguration>((o, configuration) =>
            {
                var value = configuration[LlmOptions.FakeEnvironmentKey];
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                o.UseFake = bool.TryParse(value.Trim(), out var useFake)
                    ? useFake
                    : throw new InvalidOperationException($"{LlmOptions.FakeEnvironmentKey} must be 'true' or 'false'.");
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHttpClient(OllamaHttp.ClientName);
        services.AddHttpClient(ClaudeApiHttp.ClientName)
            .AddHttpMessageHandler(() => new ClaudeApiHeaderFilter())
            .AddHttpMessageHandler(sp =>
            {
                var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
                return new ClaudeApiRetryHandler(llm.ClaudeApiMaxRetries, llm.ClaudeApiTimeout, sp.GetRequiredService<TimeProvider>());
            });
        services.AddHttpClient(ClaudeApiHttp.NoRetryClientName)
            .AddHttpMessageHandler(() => new ClaudeApiHeaderFilter());
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OllamaCatalog>();
        services.AddScoped<IOllamaCatalog>(sp => UseFake(sp)
            ? new FakeOllamaCatalog()
            : sp.GetRequiredService<OllamaCatalog>());
        services.AddScoped<ClaudeApiCatalog>();
        services.AddScoped<IClaudeApiCatalog>(sp => UseFake(sp)
            ? new FakeClaudeApiCatalog(sp.GetRequiredService<ClaudeApiKeyService>())
            : sp.GetRequiredService<ClaudeApiCatalog>());
        services.AddHostedService<LlmFakeNotice>();
        services.AddScoped<ILlmClientFactory, LlmClientFactory>();
        services.AddScoped<LlmModelTester>();
        services.AddScoped<ClaudeApiKeyService>();
        return services;
    }

    private static bool UseFake(IServiceProvider sp) => sp.GetRequiredService<IOptions<LlmOptions>>().Value.UseFake;

    /// <summary>Maps <see cref="LlmNotConfiguredException"/> from any endpoint to a 409 ProblemDetails.</summary>
    public static IApplicationBuilder UseLlmNotConfiguredProblem(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (LlmNotConfiguredException ex) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = { Status = StatusCodes.Status409Conflict, Title = "LLM not configured", Detail = ex.Message },
                });
            }
        });

    public static IEndpointRouteBuilder MapLlmEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/llm").WithTags("Llm");
        group.MapGet("/models", GetModelsAsync);
        group.MapPost("/test-model", TestModelAsync);
        endpoints.MapClaudeApiEndpoints();
        return endpoints;
    }

    private static async Task<Results<Ok<LlmModelsDto>, ValidationProblem>> GetModelsAsync(
        string? baseUrl, IOllamaCatalog catalog, CancellationToken ct)
    {
        if (baseUrl is not null && !SettingsValidation.IsHttpUrl(baseUrl))
        {
            return TypedResults.ValidationProblem(BaseUrlError);
        }

        try
        {
            var version = await catalog.PingAsync(baseUrl, ct);
            var models = await catalog.ListModelsAsync(baseUrl, ct);
            return TypedResults.Ok(LlmModelsDto.Split(version, models));
        }
        catch (OllamaUnreachableException ex)
        {
            return TypedResults.Ok(LlmModelsDto.Unreachable(ex.Message));
        }
    }

    private static async Task<Results<Ok<TestModelResultDto>, ValidationProblem>> TestModelAsync(
        TestModelRequest request, ISettingsStore settings, LlmModelTester tester, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var url = await OllamaCatalog.ResolveBaseUrlAsync(request.BaseUrl, settings, ct);
        return TypedResults.Ok(await tester.TestAsync(request.Kind!, request.Model!.Trim(), url, ct));
    }

    private static Dictionary<string, string[]> BaseUrlError => new() { ["baseUrl"] = ["Must be an absolute http or https URL."] };

    private static Dictionary<string, string[]> Validate(TestModelRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Kind is not (ModelKinds.Chat or ModelKinds.Embedding))
        {
            errors["kind"] = [$"Must be '{ModelKinds.Chat}' or '{ModelKinds.Embedding}'."];
        }

        ModelNameValidation.CheckRequired(errors, "model", request.Model);

        if (request.BaseUrl is not null && !SettingsValidation.IsHttpUrl(request.BaseUrl))
        {
            errors["baseUrl"] = BaseUrlError["baseUrl"];
        }

        return errors;
    }
}
