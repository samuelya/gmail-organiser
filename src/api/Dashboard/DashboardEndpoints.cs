using Microsoft.AspNetCore.Http.HttpResults;

namespace GmailOrganiser.Dashboard;

/// <summary>A snapshot with its coverage ratios (0–1; 0 when the denominator is 0).</summary>
public sealed record MetricSnapshotDto(
    DateTimeOffset TakenAt,
    int MessagesTotal,
    int InboxCount,
    int InboxUnreadCount,
    int CoveredByPolicy,
    int CoveredByFilter,
    int Analysed,
    int Applied,
    int ToBeDeleted,
    long LlmMillisecondsTotal,
    long PromptTokensTotal,
    double PolicyCoverageRatio,
    double FilterCoverageRatio,
    double AnalysedRatio,
    double InboxUnreadRatio)
{
    public static MetricSnapshotDto From(MetricSnapshotRow r) => new(
        r.TakenAt, r.MessagesTotal, r.InboxCount, r.InboxUnreadCount, r.CoveredByPolicy, r.CoveredByFilter, r.Analysed, r.Applied,
        r.ToBeDeleted, r.LlmMillisecondsTotal, r.PromptTokensTotal,
        Ratio(r.CoveredByPolicy, r.MessagesTotal), Ratio(r.CoveredByFilter, r.MessagesTotal), Ratio(r.Analysed, r.MessagesTotal),
        Ratio(r.InboxUnreadCount, r.InboxCount));

    private static double Ratio(int part, int whole) => whole == 0 ? 0 : (double)part / whole;
}

/// <summary>The last snapshot of one UTC day.</summary>
public sealed record MetricPointDto(
    DateOnly Day,
    DateTimeOffset TakenAt,
    int MessagesTotal,
    int InboxCount,
    int InboxUnreadCount,
    int CoveredByPolicy,
    int CoveredByFilter,
    int Analysed,
    int Applied,
    int ToBeDeleted,
    double LlmHours)
{
    public static MetricPointDto From(DateOnly day, MetricSnapshotRow r) => new(
        day, r.TakenAt, r.MessagesTotal, r.InboxCount, r.InboxUnreadCount, r.CoveredByPolicy, r.CoveredByFilter, r.Analysed,
        r.Applied, r.ToBeDeleted, Hours(r.LlmMillisecondsTotal));

    public static double Hours(long milliseconds) => milliseconds / 3_600_000d;
}

/// <param name="Current">The newest snapshot; null before the first one.</param>
/// <param name="History">One point per day, oldest first, over the last <see cref="MetricsSnapshotter.HistoryDays"/> days.</param>
/// <param name="LlmHours">LLM time over all analysis runs, as of <paramref name="Current"/>.</param>
public sealed record TriageMetricsDto(MetricSnapshotDto? Current, IReadOnlyList<MetricPointDto> History, double LlmHours);

public static class DashboardEndpoints
{
    /// <summary>Registers the snapshotter and its hourly / after-job scheduler.</summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services)
    {
        services.AddScoped<MetricsSnapshotter>();
        services.AddHostedService<MetricsScheduler>();
        return services;
    }

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/dashboard").WithTags("Dashboard");
        group.MapGet("/triage", GetTriageAsync);
        group.MapPost("/triage/snapshot", SnapshotAsync);
        return endpoints;
    }

    /// <summary>The newest triage snapshot with ratios, 90 days of daily history and the LLM hours.</summary>
    private static async Task<Ok<TriageMetricsDto>> GetTriageAsync(
        MetricsSnapshotter snapshotter, CancellationToken ct) =>
        TypedResults.Ok(await snapshotter.GetAsync(ct));

    /// <summary>Manual refresh: takes a snapshot now (the 15-minute guard is for scheduled snapshots only) and returns the metrics.</summary>
    private static async Task<Ok<TriageMetricsDto>> SnapshotAsync(
        MetricsSnapshotter snapshotter, CancellationToken ct)
    {
        await snapshotter.TakeAsync(ct);
        return TypedResults.Ok(await snapshotter.GetAsync(ct));
    }
}
