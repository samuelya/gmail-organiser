using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Claude;

/// <summary>The Claude review queue as the portal lists it.</summary>
public sealed class ExternalReviewQuery(AppDbContext db)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Parses a snake_case status; null input is "any", an unknown value is invalid.</summary>
    public static bool TryParseStatus(string? value, out ExternalReviewStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var name = value.Trim().ToLowerInvariant();
        foreach (var s in Enum.GetValues<ExternalReviewStatus>())
        {
            if (SnakeCaseEnumConverter<ExternalReviewStatus>.ToDb(s) == name)
            {
                status = s;
                return true;
            }
        }

        return false;
    }

    /// <summary>Items in <paramref name="status"/> (any when null), newest first.</summary>
    public async Task<PagedDto<ExternalReviewDto>> ListAsync(ExternalReviewStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var items = db.ExternalReviews.AsNoTracking();
        if (status is { } s)
        {
            items = items.Where(r => r.Status == s);
        }

        var total = await items.LongCountAsync(ct);
        var rows = await items.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedDto<ExternalReviewDto>(await ToDtosAsync(rows, ct), page, pageSize, total);
    }

    public async Task<ExternalReviewDto?> GetAsync(Guid id, CancellationToken ct) =>
        await db.ExternalReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } row
            ? (await ToDtosAsync([row], ct))[0]
            : null;

    public async Task<ExternalReviewSummaryDto> SummaryAsync(CancellationToken ct)
    {
        var counts = await db.ExternalReviews.AsNoTracking()
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(c => c.Status, c => c.Count, ct);
        return new ExternalReviewSummaryDto(
            counts.GetValueOrDefault(ExternalReviewStatus.Queued),
            counts.GetValueOrDefault(ExternalReviewStatus.Running),
            counts.GetValueOrDefault(ExternalReviewStatus.Reviewed),
            counts.GetValueOrDefault(ExternalReviewStatus.Unavailable));
    }

    /// <summary>
    /// DTOs in the rows' order. <c>GroupDisplay</c> is what the review page titles the group: the newest member's
    /// subject (plus the list suffix for a list group); for a single suggestion, its message's subject.
    /// </summary>
    public async Task<IReadOnlyList<ExternalReviewDto>> ToDtosAsync(IReadOnlyList<ExternalReviewRow> rows, CancellationToken ct)
    {
        var keys = rows.Where(r => r.TargetType == ExternalReviewTarget.Group && r.GroupKey is not null)
            .Select(r => r.GroupKey!).Distinct().ToList();
        var groupSubjects = keys.Count == 0
            ? []
            : (await (
                    from s in db.Suggestions.AsNoTracking()
                    join m in db.Messages.AsNoTracking() on s.MessageId equals m.Id
                    where keys.Contains(s.GroupKey!)
                    group m by new { s.SenderAddress, s.GroupKey } into g
                    select new
                    {
                        g.Key.SenderAddress,
                        g.Key.GroupKey,
                        Subject = g.OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id).Select(m => m.Subject).First(),
                    })
                .ToListAsync(ct))
            .ToDictionary(x => (x.SenderAddress, x.GroupKey!), x => x.Subject);

        var ids = rows.Where(r => r.SuggestionId is not null).Select(r => r.SuggestionId!.Value).ToList();
        var suggestionSubjects = ids.Count == 0
            ? []
            : await (
                    from s in db.Suggestions.AsNoTracking()
                    join m in db.Messages.AsNoTracking() on s.MessageId equals m.Id
                    where ids.Contains(s.Id)
                    select new { s.Id, m.Subject })
                .ToDictionaryAsync(x => x.Id, x => x.Subject, ct);

        return [.. rows.Select(r =>
        {
            string? display = null;
            if (r.TargetType == ExternalReviewTarget.Group && r.GroupKey is { } key
                && groupSubjects.TryGetValue((r.SenderAddress, key), out var subject))
            {
                display = Display(subject) + (GroupKey.IsList(key) ? AnalysisGrouper.ListDisplaySuffix : "");
            }
            else if (r.SuggestionId is { } id && suggestionSubjects.TryGetValue(id, out var single))
            {
                display = Display(single);
            }

            return ToDto(r, display);
        })];
    }

    private static string Display(string? subject) =>
        string.IsNullOrWhiteSpace(subject) ? AnalysisGrouper.NoSubjectDisplay : subject;

    private static ExternalReviewDto ToDto(ExternalReviewRow r, string? groupDisplay) => new(
        r.Id,
        SnakeCaseEnumConverter<ExternalReviewTarget>.ToDb(r.TargetType),
        r.SuggestionId,
        r.SenderAddress,
        r.GroupKey,
        groupDisplay,
        SnakeCaseEnumConverter<ExternalReviewStatus>.ToDb(r.Status),
        r.Reviewer,
        r.Verdict is { } v ? SnakeCaseEnumConverter<ReviewVerdict>.ToDb(v) : null,
        r.VerdictTopicLabel,
        r.VerdictNeedsAction,
        r.VerdictToBeDeleted,
        r.Reasoning,
        r.Error,
        SnakeCaseEnumConverter<ExternalReviewResolution>.ToDb(r.Resolution),
        r.CreatedAt,
        r.ReviewedAt,
        r.ResolvedAt);
}
