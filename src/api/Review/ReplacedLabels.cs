using GmailOrganiser.Analysis;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Review;

/// <summary>
/// Edits of a suggestion's replaced labels. The app's action and delete labels (names from Settings) are only there
/// when accepting an alternative on an applied suggestion put them there: an edit may name them back but never adds
/// one, and an edit that omits them keeps them, so a partial list never leaves the message deletable. They stay when
/// the edited outcome sets the flag again: apply never removes a label it adds.
/// </summary>
internal static class ReplacedLabels
{
    /// <summary>The stored replaced labels named as the action or delete label, by their stored name.</summary>
    public static IEnumerable<(string Id, string Name)> AppOf(SuggestionRow s, AppSettings settings) =>
        s.Replaced(null).Where(l => IsApp(l.Name, settings));

    /// <summary>The trimmed names of <paramref name="replaceLabels"/> not in <paramref name="carried"/>, distinct, in request order.</summary>
    public static IReadOnlyList<string> Unknown(IReadOnlyList<string> replaceLabels, HashSet<string> carried) =>
        [.. replaceLabels.Select(l => l.Trim()).Where(l => !carried.Contains(l)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Keeps the suggestion's own replaced labels whose name (current, else as stored) is in <paramref name="requested"/>,
    /// and the action and delete labels; never adds one. Marks the suggestion edited when that drops any. Null leaves them.
    /// </summary>
    public static void Keep(
        SuggestionRow s, IReadOnlyList<string>? requested, IReadOnlyDictionary<string, string>? names, AppSettings settings)
    {
        if (requested is null)
        {
            return;
        }

        var wanted = requested.Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var app = AppOf(s, settings).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (s.SetReplaced([.. s.Replaced(names).Where(l => app.Contains(l.Id) || wanted.Contains(l.Name.Trim()))]))
        {
            s.Edited = true;
        }
    }

    private static bool IsApp(string name, AppSettings settings) =>
        string.Equals(name.Trim(), settings.ActionLabelName.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(name.Trim(), settings.DeleteLabelName.Trim(), StringComparison.OrdinalIgnoreCase);
}
