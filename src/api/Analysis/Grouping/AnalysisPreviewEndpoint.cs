using System.Text.RegularExpressions;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis.Grouping;

public static partial class AnalysisPreviewEndpoint
{
    public const int LargestGroupsShown = 10;
    public const int MaxSenderAddressLength = 320;

    // Gmail message ids are short hex strings; the bound leaves room without accepting arbitrary text.
    public const int MaxMessageIdLength = 64;

    public static IServiceCollection AddAnalysisGrouping(this IServiceCollection services)
    {
        services.AddSingleton<IGroupRefiner, NoOpGroupRefiner>();
        services.AddScoped<AnalysisGrouper>();
        return services;
    }

    public static IEndpointRouteBuilder MapAnalysisPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/analysis/preview", PreviewAsync).WithTags("Analysis");
        return endpoints;
    }

    /// <summary>Candidates and grouping for a scope and count, without any model call; 400 on an invalid request.</summary>
    private static async Task<Results<Ok<GroupingPreviewDto>, ValidationProblem>> PreviewAsync(
        AnalysisPreviewRequest request, AppDbContext db, ISettingsStore settingsStore, AnalysisGrouper grouper,
        IAnalysisShortCircuit shortCircuit, LabelCatalog labelCatalog, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var settings = await settingsStore.GetAsync(ct);
        var (scope, count) = ValidateSelection(request.Scope, request.SenderAddress, request.MessageIds, request.Count, settings, errors);
        if (errors.Count > 0 || scope is not { } s)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Label names are only needed for the labelled scope, groups split by labels or the memory check of labelled
        // mail; Gmail failures degrade to no names (labelled groups then count as model calls).
        var labels = s == AnalysisScope.Labelled ? await PersonalLabels.LoadAsync(labelCatalog, settings, ct) : null;
        var candidates = await AnalysisCandidates.QueryAsync(
            db, s, request.SenderAddress, request.MessageIds, count, labels?.AppLabelIds ?? [], ct);
        if (labels is null && candidates.Any(m => m.LabelIds.Any(GmailLabelIds.IsUser)))
        {
            labels = await PersonalLabels.LoadAsync(labelCatalog, settings, ct);
        }

        labels ??= PersonalLabels.None;
        var allowlisted = await AllowlistLoader.LoadAsync(db, settings, ct);
        var groups = await grouper.GroupAsync(candidates, GroupingSettings.From(settings), allowlisted, labels, ct);

        // The run's own memory lookup, in one query for all groups: a covered group costs one call per member memory
        // left out (protected mail), and derives nothing. The label tree does not change the counts; the names by id do.
        var covered = await shortCircuit.TryAsync(groups, new ShortCircuitContext(settings, allowlisted, [], labels), ct);
        var modelGroups = groups.Where((_, i) => covered[i] is null).ToList();
        var fromMemory = covered.Sum(c => c?.Suggestions.Count ?? 0);

        return TypedResults.Ok(new GroupingPreviewDto(
            Messages: candidates.Count,
            Skipped: AnalysisCandidates.Skipped(s, count, candidates.Count),
            Groups: groups.Count,
            EstimatedLlmCalls: modelGroups.Count + groups.Where((_, i) => covered[i] is not null).Sum(g => g.Members.Count) - fromMemory,
            EstimatedDerived: modelGroups.Where(g => !g.Individual).Sum(g => g.Members.Count - g.RepresentativeIds.Count),
            EstimatedFromMemory: fromMemory,
            EmbeddingsAvailable: !string.IsNullOrWhiteSpace(settings.EmbeddingModel),
            LargestGroups: groups
                .OrderByDescending(g => g.Members.Count)
                .Take(LargestGroupsShown)
                .Select(g => new GroupPreviewDto(g.Key, g.SenderAddress, g.Display, g.Members.Count, g.RepresentativeIds.Count))
                .ToList()));
    }

    /// <summary>
    /// Validates a run's selection (shared by the preview and the run start) into <paramref name="errors"/>. The messages
    /// scope covers exactly its ids, so its count is the number of ids; a count would silently drop some.
    /// </summary>
    public static (AnalysisScope? Scope, int Count) ValidateSelection(
        string? scopeValue, string? senderAddress, string[]? messageIds, int? requestedCount, AppSettings settings,
        Dictionary<string, string[]> errors)
    {
        var scope = ParseScope(scopeValue, errors);
        var count = scope == AnalysisScope.Messages && messageIds is { Length: > 0 } ids
            ? ids.Distinct(StringComparer.Ordinal).Count()
            : requestedCount ?? settings.AnalysisDefaultCount;
        if (scope != AnalysisScope.Messages
            && count is < SettingsValidation.MinAnalysisDefaultCount or > SettingsValidation.MaxAnalysisDefaultCount)
        {
            errors["count"] = [$"Must be between {SettingsValidation.MinAnalysisDefaultCount} and {SettingsValidation.MaxAnalysisDefaultCount}."];
        }

        if (scope == AnalysisScope.Sender
            && (string.IsNullOrWhiteSpace(senderAddress) || senderAddress.Length > MaxSenderAddressLength))
        {
            errors["senderAddress"] = [$"Required for the sender scope, at most {MaxSenderAddressLength} characters."];
        }

        if (scope == AnalysisScope.Messages && !AreValidMessageIds(messageIds))
        {
            errors["messageIds"] = [
                $"Required for the messages scope: 1 to {AnalysisCandidates.MaxMessageIds} ids of letters, digits, '-' or '_', "
                + $"at most {MaxMessageIdLength} characters each."];
        }

        return (scope, count);
    }

    /// <summary>1 to <see cref="AnalysisCandidates.MaxMessageIds"/> Gmail-shaped ids.</summary>
    public static bool AreValidMessageIds(string[]? messageIds) =>
        messageIds is { Length: > 0 and <= AnalysisCandidates.MaxMessageIds }
        && messageIds.All(id => id is { Length: <= MaxMessageIdLength } && MessageId().IsMatch(id));

    private static AnalysisScope? ParseScope(string? value, Dictionary<string, string[]> errors)
    {
        var scope = Enum.GetValues<AnalysisScope>().Cast<AnalysisScope?>().FirstOrDefault(v =>
            string.Equals(SnakeCaseEnumConverter<AnalysisScope>.ToDb(v!.Value), value?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scope is null)
        {
            errors["scope"] = ["Must be one of inbox, all, sender, messages, labelled."];
        }

        return scope;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MessageId();
}
