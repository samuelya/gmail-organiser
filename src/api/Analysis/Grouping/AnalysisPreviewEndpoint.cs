using GmailOrganiser.Data;
using GmailOrganiser.Settings;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis.Grouping;

public static class AnalysisPreviewEndpoint
{
    public const int LargestGroupsShown = 10;
    public const int MaxSenderAddressLength = 320;

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
        AnalysisPreviewRequest request, AppDbContext db, ISettingsStore settingsStore, AnalysisGrouper grouper, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var scope = ParseScope(request.Scope, errors);
        var settings = await settingsStore.GetAsync(ct);
        var count = request.Count ?? settings.AnalysisDefaultCount;
        if (count is < SettingsValidation.MinAnalysisDefaultCount or > SettingsValidation.MaxAnalysisDefaultCount)
        {
            errors["count"] = [$"Must be between {SettingsValidation.MinAnalysisDefaultCount} and {SettingsValidation.MaxAnalysisDefaultCount}."];
        }

        if (scope == AnalysisScope.Sender
            && (string.IsNullOrWhiteSpace(request.SenderAddress) || request.SenderAddress.Length > MaxSenderAddressLength))
        {
            errors["senderAddress"] = [$"Required for the sender scope, at most {MaxSenderAddressLength} characters."];
        }

        if (scope == AnalysisScope.Messages
            && (request.MessageIds is not { Length: > 0 and <= AnalysisCandidates.MaxMessageIds }
                || request.MessageIds.Any(string.IsNullOrWhiteSpace)))
        {
            errors["messageIds"] = [$"Required for the messages scope: 1 to {AnalysisCandidates.MaxMessageIds} ids."];
        }

        if (errors.Count > 0 || scope is not { } s)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var candidates = await AnalysisCandidates.QueryAsync(db, s, request.SenderAddress, request.MessageIds, count, ct);
        var addresses = candidates.Select(m => m.FromAddress).Distinct().ToArray();
        var allowlisted = (await db.Senders.AsNoTracking()
                .Where(x => x.Allowlisted && addresses.Contains(x.Address))
                .Select(x => x.Address)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var groups = await grouper.GroupAsync(candidates, GroupingSettings.From(settings), allowlisted, ct);

        return TypedResults.Ok(new GroupingPreviewDto(
            Messages: candidates.Count,
            Groups: groups.Count,
            EstimatedLlmCalls: groups.Count,
            EstimatedDerived: groups.Where(g => !g.Individual).Sum(g => g.Members.Count - g.RepresentativeIds.Count),
            EstimatedFromMemory: 0,
            EmbeddingsAvailable: !string.IsNullOrWhiteSpace(settings.EmbeddingModel),
            LargestGroups: groups
                .OrderByDescending(g => g.Members.Count)
                .Take(LargestGroupsShown)
                .Select(g => new GroupPreviewDto(g.Key, g.SenderAddress, g.Display, g.Members.Count, g.RepresentativeIds.Count))
                .ToList()));
    }

    private static AnalysisScope? ParseScope(string? value, Dictionary<string, string[]> errors)
    {
        var scope = Enum.GetValues<AnalysisScope>().Cast<AnalysisScope?>().FirstOrDefault(v =>
            string.Equals(SnakeCaseEnumConverter<AnalysisScope>.ToDb(v!.Value), value?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scope is null)
        {
            errors["scope"] = ["Must be one of inbox, all, sender, messages."];
        }

        return scope;
    }
}
