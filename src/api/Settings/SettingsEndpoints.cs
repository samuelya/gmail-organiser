using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Settings;

public static class SettingsEndpoints
{
    public const string ApplicationName = "gmail-organiser";

    /// <summary>Registers the settings store, the Google client service and Data Protection (key ring on disk).</summary>
    public static IServiceCollection AddSettings(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SettingsEnvOptions>().Bind(configuration);
        services.AddOptions<DataProtectionKeyOptions>().BindConfiguration(DataProtectionKeyOptions.SectionName);
        services.AddDataProtection().SetApplicationName(ApplicationName);

        // Same as PersistKeysToFileSystem, but the path is read when options are built so hosts can override it.
        services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<DataProtectionKeyOptions>, IHostEnvironment, ILoggerFactory>((o, keys, env, loggers) =>
                o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(keys.Value.ResolvePath(env.ContentRootPath)), loggers));

        services.AddScoped<ISettingsStore, SettingsStore>();
        services.AddScoped<GoogleClientService>();
        return services;
    }

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/settings").WithTags("Settings");
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync);
        group.MapPut("/google-client", SetGoogleClientAsync);
        return endpoints;
    }

    private static async Task<Ok<SettingsDto>> GetAsync(ISettingsStore store, GoogleClientService google, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        return TypedResults.Ok(SettingsDto.From(settings, google.Resolve(settings)));
    }

    private static async Task<Results<Ok<SettingsDto>, ValidationProblem>> UpdateAsync(
        UpdateSettingsRequest request, ISettingsStore store, GoogleClientService google, CancellationToken ct)
    {
        var errors = SettingsValidation.Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var settings = await store.UpdateAsync(s => s with
        {
            OllamaBaseUrl = request.OllamaBaseUrl?.Trim() ?? s.OllamaBaseUrl,
            ChatModel = request.ChatModel is null ? s.ChatModel : SettingsValidation.NormaliseModelName(request.ChatModel),
            EmbeddingModel = request.EmbeddingModel is null ? s.EmbeddingModel : SettingsValidation.NormaliseModelName(request.EmbeddingModel),
            SetupWizardSeen = request.SetupWizardSeen ?? s.SetupWizardSeen,
            FetchChunkSize = request.FetchChunkSize ?? s.FetchChunkSize,
        }, ct);
        return TypedResults.Ok(SettingsDto.From(settings, google.Resolve(settings)));
    }

    private static async Task<Results<Ok<SettingsDto>, ValidationProblem, ProblemHttpResult>> SetGoogleClientAsync(
        GoogleClientRequest request, ISettingsStore store, GoogleClientService google, CancellationToken ct)
    {
        var errors = SettingsValidation.Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (!await google.TrySetAsync(request.ClientId!.Trim(), request.ClientSecret!.Trim(), ct))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Google client is set in .env",
                detail: "GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET are set in the environment and cannot be changed here.");
        }

        var settings = await store.GetAsync(ct);
        return TypedResults.Ok(SettingsDto.From(settings, google.Resolve(settings)));
    }
}

/// <summary>
/// Where the Data Protection key ring lives, bound from <c>DataProtection</c>. Relative paths resolve against the
/// content root; Development points at the git-ignored repository-root <c>data/dp-keys</c> (not <c>src/api/data</c>,
/// which is <c>src/api/Data</c> on case-insensitive file systems). The image uses <c>/keys</c> (the <c>dp-keys</c> volume).
/// </summary>
public sealed class DataProtectionKeyOptions
{
    public const string SectionName = "DataProtection";
    public const string DefaultKeysPath = "./data/dp-keys";

    public string KeysPath { get; set; } = DefaultKeysPath;

    public string ResolvePath(string contentRoot) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(KeysPath) ? DefaultKeysPath : KeysPath, contentRoot);
}
