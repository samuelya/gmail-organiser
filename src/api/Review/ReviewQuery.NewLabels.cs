using System.Linq.Expressions;
using GmailOrganiser.Analysis;

namespace GmailOrganiser.Review;

/// <summary>Whether a suggestion's new label is still new (#367).</summary>
public sealed partial class ReviewQuery
{
    /// <summary>
    /// Whether the suggestion's new topic label is still missing from Gmail: <see cref="SuggestionRow.IsNewLabel"/> is
    /// set at analysis and never updated, so a label created since no longer counts. Without the label list
    /// (<paramref name="labelNames"/> null) the stored flag stands.
    /// </summary>
    public static bool IsStillNew(SuggestionRow s, IReadOnlyDictionary<string, string>? labelNames) =>
        s.IsNewLabel
        && (labelNames is null || !labelNames.Values.Any(n => string.Equals(n.Trim(), s.TopicLabel.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// <see cref="IsStillNew"/> as SQL, negated: the suggestion proposes no new label, or one Gmail has since. The bulk
    /// approve skip and its <see cref="BulkApproveResponse.ExcludedNewLabel"/> count both take it.
    /// </summary>
    public static Expression<Func<SuggestionRow, bool>> NotStillNew(IReadOnlyDictionary<string, string>? labelNames)
    {
        if (labelNames is null)
        {
            return s => !s.IsNewLabel;
        }

        var existing = labelNames.Values.Select(n => n.Trim().ToLowerInvariant()).Distinct().ToList();
        return s => !s.IsNewLabel || existing.Contains(s.TopicLabel.Trim().ToLower());
    }
}
