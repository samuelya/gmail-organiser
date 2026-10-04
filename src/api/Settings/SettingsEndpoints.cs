using GmailOrganiser.Analysis.Attachments;
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
        // Only whether the Claude token is set is kept; the value is read here and dropped.
        services.AddOptions<SettingsEnvOptions>().Bind(configuration).PostConfigure(o =>
            o.ClaudeCodeOAuthTokenSet = !string.IsNullOrWhiteSpace(configuration[SettingsEnvOptions.ClaudeCodeOAuthTokenKey]));
        services.AddOptions<DataProtectionKeyOptions>().BindConfiguration(DataProtectionKeyOptions.SectionName);
        services.AddDataProtection().SetApplicationName(ApplicationName);

        // Same as PersistKeysToFileSystem, but the path is read when options are built so hosts can override it.
        services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<DataProtectionKeyOptions>, IHostEnvironment, ILoggerFactory>((o, keys, env, loggers) =>
                o.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(keys.Value.ResolvePath(env.ContentRootPath)), loggers));

        services.AddScoped<ISettingsStore, SettingsStore>();
        services.AddScoped<GoogleClientService>();
        services.AddScoped<DataPurgeService>();
        return services;
    }

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/settings").WithTags("Settings");
        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync);
        group.MapPut("/google-client", SetGoogleClientAsync);
        group.MapPost("/purge", PurgeAsync);
        return endpoints;
    }

    private static async Task<Ok<SettingsDto>> GetAsync(
        ISettingsStore store, GoogleClientService google, IOptions<SettingsEnvOptions> env, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        return TypedResults.Ok(SettingsDto.From(settings, google.Resolve(settings), env.Value.ClaudeCodeOAuthTokenSet));
    }

    private static async Task<Results<Ok<SettingsDto>, ValidationProblem>> UpdateAsync(
        UpdateSettingsRequest request, ISettingsStore store, GoogleClientService google, IOptions<SettingsEnvOptions> env, CancellationToken ct)
    {
        var errors = SettingsValidation.Validate(request, await store.GetAsync(ct));
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // The pre-check above reads a snapshot; a concurrent PUT may change the other label name before the row lock,
        // so the merged pair is checked again inside the locked update and the whole request is dropped on a clash.
        var clash = false;
        var settings = await store.UpdateAsync(s =>
        {
            var merged = Merge(s, request);
            clash = (request.ActionLabelName is not null || request.DeleteLabelName is not null)
                && SettingsValidation.LabelNamesClash(merged.ActionLabelName, merged.DeleteLabelName);
            return clash ? s : merged;
        }, ct);
        if (clash)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [SettingsValidation.LabelNamesClashField] = [SettingsValidation.LabelNamesClashMessage],
            });
        }

        return TypedResults.Ok(SettingsDto.From(settings, google.Resolve(settings), env.Value.ClaudeCodeOAuthTokenSet));
    }

    /// <summary>Applies a validated request; omitted fields keep their saved value.</summary>
    private static AppSettings Merge(AppSettings s, UpdateSettingsRequest request) => s with
    {
        OllamaBaseUrl = request.OllamaBaseUrl?.Trim() ?? s.OllamaBaseUrl,
        ChatModel = request.ChatModel is null ? s.ChatModel : SettingsValidation.NormaliseModelName(request.ChatModel),
        EmbeddingModel = request.EmbeddingModel is null ? s.EmbeddingModel : SettingsValidation.NormaliseModelName(request.EmbeddingModel),
        VisionModel = request.VisionModel is null ? s.VisionModel : SettingsValidation.NormaliseModelName(request.VisionModel),
        SetupWizardSeen = request.SetupWizardSeen ?? s.SetupWizardSeen,
        FetchChunkSize = request.FetchChunkSize ?? s.FetchChunkSize,
        AnalysisDefaultCount = request.AnalysisDefaultCount ?? s.AnalysisDefaultCount,
        AnalysisBodyMaxChars = request.AnalysisBodyMaxChars ?? s.AnalysisBodyMaxChars,
        AnalysisGroupingMode = request.AnalysisGroupingMode ?? s.AnalysisGroupingMode,
        AnalysisRepresentativesPerGroup = request.AnalysisRepresentativesPerGroup ?? s.AnalysisRepresentativesPerGroup,
        AnalysisMinGroupSize = request.AnalysisMinGroupSize ?? s.AnalysisMinGroupSize,
        AnalysisDerivedConfidencePenalty = request.AnalysisDerivedConfidencePenalty ?? s.AnalysisDerivedConfidencePenalty,
        AnalysisClusterDistance = request.AnalysisClusterDistance ?? s.AnalysisClusterDistance,
        AnalysisMemoryShortCircuit = request.AnalysisMemoryShortCircuit ?? s.AnalysisMemoryShortCircuit,
        AnalysisMemoryMinApprovals = request.AnalysisMemoryMinApprovals ?? s.AnalysisMemoryMinApprovals,
        BulkApproveThreshold = request.BulkApproveThreshold ?? s.BulkApproveThreshold,
        AutoArchiveOnActionDone = request.AutoArchiveOnActionDone ?? s.AutoArchiveOnActionDone,
        AnalysisPromptTemplate = request.AnalysisPromptTemplate is null
            ? s.AnalysisPromptTemplate
            : SettingsValidation.NormalisePromptTemplate(request.AnalysisPromptTemplate),
        Attachments = request.Attachments is { } attachments ? Apply(s.Attachments, attachments) : s.Attachments,
        ClaudeReviewerMode = request.ClaudeReviewerMode ?? s.ClaudeReviewerMode,
        ClaudeSuggestLowConfidence = request.ClaudeSuggestLowConfidence ?? s.ClaudeSuggestLowConfidence,
        ClaudeSuggestThreshold = request.ClaudeSuggestThreshold ?? s.ClaudeSuggestThreshold,
        ClaudeSuggestNewLabels = request.ClaudeSuggestNewLabels ?? s.ClaudeSuggestNewLabels,
        ClaudeRunTimeoutSeconds = request.ClaudeRunTimeoutSeconds ?? s.ClaudeRunTimeoutSeconds,
        ClaudeMaxItemsPerRun = request.ClaudeMaxItemsPerRun ?? s.ClaudeMaxItemsPerRun,
        ClaudeMaxTurns = request.ClaudeMaxTurns ?? s.ClaudeMaxTurns,
        ClaudeModel = request.ClaudeModel is null ? s.ClaudeModel : SettingsValidation.NormaliseModelName(request.ClaudeModel),
        Protection = request.Protection is { } protection ? Apply(s.Protection, protection) : s.Protection,
        ActionLabelName = request.ActionLabelName?.Trim() ?? s.ActionLabelName,
        DeleteLabelName = request.DeleteLabelName?.Trim() ?? s.DeleteLabelName,
    };

    /// <summary>Applies a validated request; listed types change, the others keep their saved value.</summary>
    private static AttachmentSettings Apply(AttachmentSettings s, UpdateAttachmentSettingsRequest request) => s with
    {
        Enabled = request.Enabled ?? s.Enabled,
        Types = request.Types is null
            ? s.Types
            : AttachmentSettings.WithDefaults(request.Types
                .Select(t => AttachmentTypeJsonConverter.TryParse(t.Type, out var type) ? new AttachmentTypeSetting(type.Value, t.Enabled!.Value) : null)
                .OfType<AttachmentTypeSetting>()
                .Concat(s.Types)),
        MaxBytes = request.MaxBytes ?? s.MaxBytes,
        MaxImageBytes = request.MaxImageBytes ?? s.MaxImageBytes,
        MaxChars = request.MaxChars ?? s.MaxChars,
        MaxPerMessage = request.MaxPerMessage ?? s.MaxPerMessage,
        ImageMode = ImageModeJsonConverter.TryParse(request.ImageMode, out var mode) ? mode.Value : s.ImageMode,
    };

    private static ProtectionSettings Apply(ProtectionSettings s, UpdateProtectionSettingsRequest request) => s with
    {
        Attachments = request.Attachments ?? s.Attachments,
        Starred = request.Starred ?? s.Starred,
        Important = request.Important ?? s.Important,
        RepliedThreads = request.RepliedThreads ?? s.RepliedThreads,
    };

    private static async Task<Results<Ok<SettingsDto>, ValidationProblem, ProblemHttpResult>> SetGoogleClientAsync(
        GoogleClientRequest request, ISettingsStore store, GoogleClientService google, IOptions<SettingsEnvOptions> env, CancellationToken ct)
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
        return TypedResults.Ok(SettingsDto.From(settings, google.Resolve(settings), env.Value.ClaudeCodeOAuthTokenSet));
    }

    private static async Task<Results<Ok<PurgeResponse>, ValidationProblem, ProblemHttpResult>> PurgeAsync(
        PurgeRequest? request, DataPurgeService purge, TimeProvider time, CancellationToken ct)
    {
        // A missing or null body is a wrong word too, so the form always gets the confirm field error.
        if (request?.Confirm?.Trim() != PurgeRequest.ConfirmationWord)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["confirm"] = [$"Type {PurgeRequest.ConfirmationWord} to confirm."],
            });
        }

        return await purge.PurgeAsync(ct) switch
        {
            PurgeResult.Done done => TypedResults.Ok(new PurgeResponse(done.Tables, time.GetUtcNow())),
            PurgeResult.GmailChunkPending => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "A Gmail batch is half-applied",
                detail: "A failed apply, undo or clean-up job stopped part-way through a Gmail batch. Resume it in Jobs so the batch finishes (and can be undone) before purging."),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Jobs are active",
                detail: "Wait for running jobs to finish or cancel them first."),
        };
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
