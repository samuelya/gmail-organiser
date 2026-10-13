using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>Compare-run alternatives on the review DTOs (#249): old vs new, shown until accepted or discarded.</summary>
public sealed partial class ReviewQuery
{
    private IQueryable<SuggestionRow> Suggestions(bool hasAlternative, IReadOnlyList<MailType>? mailTypes)
    {
        var suggestions = OfTypes(db.Suggestions.AsNoTracking(), mailTypes);
        return hasAlternative ? suggestions.Where(s => db.SuggestionAlternatives.Any(a => a.SuggestionId == s.Id)) : suggestions;
    }

    /// <summary>
    /// The sender's suggestions in <paramref name="statuses"/>; with <paramref name="hasAlternative"/> or
    /// <paramref name="mailTypes"/> only the groups where at least one member matches, listed whole (every member), so a
    /// group card and its approve/reject/edit act on the same members. Several statuses list a group as one card per status.
    /// </summary>
    private IQueryable<SuggestionRow> InStatus(string address, IReadOnlyList<SuggestionStatus> statuses, bool hasAlternative, IReadOnlyList<MailType>? mailTypes)
    {
        var inStatus = db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == address && statuses.Contains(s.Status));
        if (!hasAlternative && mailTypes is not { Count: > 0 })
        {
            return inStatus;
        }

        var keys = Suggestions(hasAlternative, mailTypes).Where(s => s.SenderAddress == address && statuses.Contains(s.Status))
            .Select(s => s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId);
        return inStatus.Where(s => keys.Contains(s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId));
    }

    /// <summary>Only suggestions of one of <paramref name="mailTypes"/>; none or an empty list is no filter.</summary>
    private static IQueryable<SuggestionRow> OfTypes(IQueryable<SuggestionRow> suggestions, IReadOnlyList<MailType>? mailTypes) =>
        mailTypes is { Count: > 0 } types ? suggestions.Where(s => s.MailType != null && types.Contains(s.MailType.Value)) : suggestions;

    /// <summary>
    /// Per group key and status (one card), how many of its members in <paramref name="inStatus"/> have an alternative
    /// (what group accept takes for that status).
    /// </summary>
    private async Task<Dictionary<(string Key, SuggestionStatus Status), int>> AlternativeCountsAsync(
        IQueryable<SuggestionRow> inStatus, List<string> keys, CancellationToken ct) =>
        (await inStatus
            .Where(s => keys.Contains(s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId)
                && db.SuggestionAlternatives.Any(a => a.SuggestionId == s.Id))
            .GroupBy(s => new { Key = s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId, s.Status })
            .Select(g => new { g.Key.Key, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct))
        .ToDictionary(g => (g.Key, g.Status), g => g.Count);

    /// <summary>An alternative with the owner comment of the compare run that wrote it (#559).</summary>
    public sealed record LoadedAlternative(SuggestionAlternativeRow Row, string? Comment);

    /// <summary>The alternatives with their run's comment, in one query (left join: the run may be gone).</summary>
    private async Task<Dictionary<Guid, LoadedAlternative>> AlternativesAsync(IEnumerable<Guid> suggestionIds, CancellationToken ct)
    {
        var ids = suggestionIds.ToArray();
        return await (
                from a in db.SuggestionAlternatives.AsNoTracking()
                where ids.Contains(a.SuggestionId)
                from r in db.AnalysisRuns.Where(r => r.Id == a.RunId).DefaultIfEmpty()
                select new LoadedAlternative(a, r == null ? null : r.OwnerComment))
            .ToDictionaryAsync(x => x.Row.SuggestionId, ct);
    }

    /// <param name="current">The message's personal label names (<see cref="SuggestionDto.CurrentLabels"/>).</param>
    private static SuggestionAlternativeDto ToDto(
        LoadedAlternative loaded, MessageRow m, IReadOnlyList<string> current, IReadOnlyDictionary<string, string>? labelNames)
    {
        var a = loaded.Row;
        var replaced = Replaced(a.Replaced(labelNames), a.TopicLabel, m, labelNames);
        return new SuggestionAlternativeDto(
            a.TopicLabel,
            a.DocumentTypeLabel,
            replaced,
            LabelChanges.For(a.TopicLabel, replaced, current),
            a.NeedsAction,
            a.ToBeDeleted,
            a.UnsubscribeSuggested,
            a.Confidence,
            a.Reason,
            a.PromptVersion,
            a.Model,
            a.CreatedAt,
            MailType: a.MailType is { } t ? SnakeCaseEnumConverter<MailType>.ToDb(t) : null,
            Comment: loaded.Comment,
            Copied: a.Copied);
    }

    /// <summary>
    /// The listed members' alternative: the newest member's of the most common outcome (label, flags, type; its comment), with
    /// the union of the replaced labels; <see cref="SuggestionAlternativeDto.Mixed"/> when members disagree on the outcome, the comment
    /// or whether the alternative was copied (#578), and <see cref="SuggestionAlternativeDto.Copied"/> only when every member's was.
    /// <see cref="SuggestionAlternativeDto.Count"/> is <paramref name="count"/>, every member in the status with one.
    /// </summary>
    private static SuggestionAlternativeDto? GroupAlternative(IReadOnlyList<SuggestionDto> members, int count)
    {
        var alternatives = members.Select(d => d.Alternative).OfType<SuggestionAlternativeDto>().ToList();
        if (alternatives.Count == 0)
        {
            return null;
        }

        var outcomes = alternatives
            .GroupBy(a => (a.TopicLabel, a.NeedsAction, a.ToBeDeleted, a.DocumentTypeLabel, a.MailType))
            .OrderByDescending(g => g.Count())
            .ToList();
        var shown = outcomes[0].First();
        return shown with
        {
            ReplaceLabels = [.. alternatives.SelectMany(a => a.ReplaceLabels).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            LabelChange = Combined(alternatives.Select(a => a.LabelChange)),
            Mixed = outcomes.Count > 1
                || alternatives.Select(a => a.Comment).Distinct(StringComparer.Ordinal).Skip(1).Any()
                || alternatives.Select(a => a.Copied).Distinct().Skip(1).Any(),
            Copied = alternatives.TrueForAll(a => a.Copied),
            Count = Math.Max(count, alternatives.Count),
        };
    }

    /// <summary>The members' label change when they agree; else relabel when any replaces a label, add otherwise.</summary>
    private static LabelChange Combined(IEnumerable<LabelChange> changes)
    {
        var distinct = changes.Distinct().ToList();
        return distinct.Count == 1 ? distinct[0]
            : distinct.Any(c => c is LabelChange.Move or LabelChange.Relabel) ? LabelChange.Relabel
            : LabelChange.Add;
    }
}
