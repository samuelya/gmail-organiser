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
    /// The sender's suggestions in <paramref name="status"/>; with <paramref name="hasAlternative"/> or
    /// <paramref name="mailTypes"/> only the groups where at least one member matches, listed whole (every member), so a
    /// group card and its approve/reject/edit act on the same members.
    /// </summary>
    private IQueryable<SuggestionRow> InStatus(string address, SuggestionStatus status, bool hasAlternative, IReadOnlyList<MailType>? mailTypes)
    {
        var inStatus = db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == address && s.Status == status);
        if (!hasAlternative && mailTypes is not { Count: > 0 })
        {
            return inStatus;
        }

        var keys = Suggestions(hasAlternative, mailTypes).Where(s => s.SenderAddress == address && s.Status == status)
            .Select(s => s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId);
        return inStatus.Where(s => keys.Contains(s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId));
    }

    /// <summary>Only suggestions of one of <paramref name="mailTypes"/>; none or an empty list is no filter.</summary>
    private static IQueryable<SuggestionRow> OfTypes(IQueryable<SuggestionRow> suggestions, IReadOnlyList<MailType>? mailTypes) =>
        mailTypes is { Count: > 0 } types ? suggestions.Where(s => s.MailType != null && types.Contains(s.MailType.Value)) : suggestions;

    /// <summary>Per group key, how many of its members in <paramref name="inStatus"/> have an alternative (what group accept takes).</summary>
    private async Task<Dictionary<string, int>> AlternativeCountsAsync(IQueryable<SuggestionRow> inStatus, List<string> keys, CancellationToken ct) =>
        await inStatus
            .Where(s => keys.Contains(s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId)
                && db.SuggestionAlternatives.Any(a => a.SuggestionId == s.Id))
            .GroupBy(s => s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, StringComparer.Ordinal, ct);

    private async Task<Dictionary<Guid, SuggestionAlternativeRow>> AlternativesAsync(IEnumerable<Guid> suggestionIds, CancellationToken ct)
    {
        var ids = suggestionIds.ToArray();
        return await db.SuggestionAlternatives.AsNoTracking()
            .Where(a => ids.Contains(a.SuggestionId))
            .ToDictionaryAsync(a => a.SuggestionId, ct);
    }

    /// <param name="current">The message's personal label names (<see cref="SuggestionDto.CurrentLabels"/>).</param>
    private static SuggestionAlternativeDto ToDto(
        SuggestionAlternativeRow a, MessageRow m, IReadOnlyList<string> current, IReadOnlyDictionary<string, string>? labelNames)
    {
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
            MailType: a.MailType is { } t ? SnakeCaseEnumConverter<MailType>.ToDb(t) : null);
    }

    /// <summary>
    /// The listed members' alternative: the newest member's of the most common outcome (label, flags, type), with
    /// the union of the replaced labels; <see cref="SuggestionAlternativeDto.Mixed"/> when members disagree.
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
            Mixed = outcomes.Count > 1,
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
