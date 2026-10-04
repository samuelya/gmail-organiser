using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>Compare-run alternatives on the review DTOs (#249): old vs new, shown until accepted or discarded.</summary>
public sealed partial class ReviewQuery
{
    private IQueryable<SuggestionRow> Suggestions(bool hasAlternative)
    {
        var suggestions = db.Suggestions.AsNoTracking();
        return hasAlternative ? suggestions.Where(s => db.SuggestionAlternatives.Any(a => a.SuggestionId == s.Id)) : suggestions;
    }

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
        var stored = a.ReplaceLabelIds
            .Select((id, i) => (id, labelNames?.GetValueOrDefault(id) ?? (i < a.ReplaceLabels.Length ? a.ReplaceLabels[i] : id)))
            .ToList();
        var replaced = Replaced(stored, a.TopicLabel, m, labelNames);
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
            a.CreatedAt);
    }

    /// <summary>
    /// The listed members' alternative: the newest member's of the most common outcome (label, flags, type), with
    /// the union of the replaced labels; <see cref="SuggestionAlternativeDto.Mixed"/> when members disagree.
    /// </summary>
    private static SuggestionAlternativeDto? GroupAlternative(IReadOnlyList<SuggestionDto> members)
    {
        var alternatives = members.Select(d => d.Alternative).OfType<SuggestionAlternativeDto>().ToList();
        if (alternatives.Count == 0)
        {
            return null;
        }

        var outcomes = alternatives
            .GroupBy(a => (a.TopicLabel, a.NeedsAction, a.ToBeDeleted, a.DocumentTypeLabel))
            .OrderByDescending(g => g.Count())
            .ToList();
        var shown = outcomes[0].First();
        return shown with
        {
            ReplaceLabels = [.. alternatives.SelectMany(a => a.ReplaceLabels).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            LabelChange = Combined(alternatives.Select(a => a.LabelChange)),
            Mixed = outcomes.Count > 1,
            Count = alternatives.Count,
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
