using System.Linq.Expressions;
using GmailOrganiser.Analysis;

namespace GmailOrganiser.Review;

/// <summary>Whether a suggestion's new label is still new (#367).</summary>
public sealed partial class ReviewQuery
{
    /// <summary>
    /// Whether the suggestion's new topic label or one of its additional labels (#529) is still missing from Gmail:
    /// <see cref="SuggestionRow.IsNewLabel"/> is set at analysis and never updated, so a label created since no longer
    /// counts. Without the label list (<paramref name="labelNames"/> null) the stored flag stands and any additional label
    /// counts as new.
    /// </summary>
    public static bool IsStillNew(SuggestionRow s, IReadOnlyDictionary<string, string>? labelNames)
    {
        var existing = labelNames?.Values.Select(n => n.Trim()).ToList();
        return existing is null
            ? s.IsNewLabel || s.ExtraLabels.Length > 0
            : (s.IsNewLabel && !existing.Contains(s.TopicLabel.Trim(), StringComparer.OrdinalIgnoreCase))
                || s.ExtraLabels.Any(e => !existing.Contains(e, StringComparer.Ordinal));
    }

    /// <summary>
    /// <see cref="IsStillNew"/> as SQL, negated: the suggestion proposes no new label, or only ones Gmail has since. The
    /// bulk approve skip and its <see cref="BulkApproveResponse.ExcludedNewLabel"/> count both take it. Additional labels
    /// compare ordinally: they are stored in Gmail's spelling, so one Gmail recased since stays pending (the safe side).
    /// </summary>
    public static Expression<Func<SuggestionRow, bool>> NotStillNew(IReadOnlyDictionary<string, string>? labelNames)
    {
        if (labelNames is null)
        {
            return s => !s.IsNewLabel && s.ExtraLabels.Length == 0;
        }

        var existing = labelNames.Values.Select(n => n.Trim().ToLowerInvariant()).Distinct().ToList();
        var names = labelNames.Values.Select(n => n.Trim()).Distinct().ToList();
        return s => (!s.IsNewLabel || existing.Contains(s.TopicLabel.Trim().ToLower())) && s.ExtraLabels.All(e => names.Contains(e));
    }
}
