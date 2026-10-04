using System.Net;
using Google;
using Google.Apis.Gmail.v1.Data;

namespace GmailOrganiser.Gmail;

public sealed partial class GoogleGmailClient
{
    public Task<IReadOnlyList<GmailFilter>> ListFiltersAsync(CancellationToken ct) =>
        RunAsync(service => retry.ExecuteAsync<IReadOnlyList<GmailFilter>>(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.SettingsCallUnits, token);
            var response = await service.Users.Settings.Filters.List(Me).ExecuteAsync(token);
            // Gmail omits the list when the account has no filters.
            var filters = response.Filter?.Select(ToGmailFilter).ToList() ?? [];
            logger.LogInformation("Listed {Count} Gmail filters", filters.Count);
            return filters;
        }, ct), ct);

    public Task<GmailFilter> CreateFilterAsync(GmailFilterCriteria criteria, GmailFilterAction action, CancellationToken ct)
    {
        GmailFilter.EnsureValidCreate(criteria, action);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.SettingsCallUnits, token);
            var created = await service.Users.Settings.Filters.Create(ToGoogleFilter(criteria, action), Me).ExecuteAsync(token);
            logger.LogInformation("Created a Gmail filter");
            return ToGmailFilter(created);
        }, ct), ct);
    }

    public Task DeleteFilterAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.SettingsCallUnits, token);
            try
            {
                await service.Users.Settings.Filters.Delete(Me, id).ExecuteAsync(token);
                logger.LogInformation("Deleted a Gmail filter");
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Gmail no longer has the filter to delete");
            }

            return true;
        }, ct), ct);
    }

    /// <summary>Maps Gmail's filter resource; blank strings and empty lists read as absent.</summary>
    public static GmailFilter ToGmailFilter(Filter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var c = filter.Criteria ?? new FilterCriteria();
        var a = filter.Action ?? new FilterAction();
        var criteria = new GmailFilterCriteria(
            Blank(c.From), Blank(c.To), Blank(c.Subject), Blank(c.Query), Blank(c.NegatedQuery),
            c.HasAttachment, c.ExcludeChats, c.Size, ParseSizeComparison(c.SizeComparison));
        var action = new GmailFilterAction([.. a.AddLabelIds ?? []], [.. a.RemoveLabelIds ?? []], Blank(a.Forward));
        return new GmailFilter(filter.Id ?? "", criteria, action);
    }

    /// <summary>The filter resource <c>filters.create</c> takes; absent parts are left out.</summary>
    public static Filter ToGoogleFilter(GmailFilterCriteria criteria, GmailFilterAction action)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(action);
        return new Filter
        {
            Criteria = new FilterCriteria
            {
                From = criteria.From,
                To = criteria.To,
                Subject = criteria.Subject,
                Query = criteria.Query,
                NegatedQuery = criteria.NegatedQuery,
                HasAttachment = criteria.HasAttachment,
                ExcludeChats = criteria.ExcludeChats,
                Size = criteria.Size,
                SizeComparison = criteria.SizeComparison switch
                {
                    GmailSizeComparison.Smaller => "smaller",
                    GmailSizeComparison.Larger => "larger",
                    _ => null,
                },
            },
            Action = new FilterAction
            {
                AddLabelIds = action.AddLabelIds.Count > 0 ? [.. action.AddLabelIds] : null,
                RemoveLabelIds = action.RemoveLabelIds.Count > 0 ? [.. action.RemoveLabelIds] : null,
                Forward = action.Forward,
            },
        };
    }

    private static GmailSizeComparison? ParseSizeComparison(string? value) => value?.ToLowerInvariant() switch
    {
        "smaller" => GmailSizeComparison.Smaller,
        "larger" => GmailSizeComparison.Larger,
        _ => null,
    };

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
