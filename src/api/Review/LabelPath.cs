using System.Text.RegularExpressions;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Review;

/// <summary>The topic label rule shared by the LLM output parser and the review edit.</summary>
public static partial class LabelPath
{
    public const int MaxLength = 225;

    /// <summary>Up to five <c>/</c>-separated segments, none blank or starting with whitespace, at most 225 chars.</summary>
    public static bool IsValid(string path) =>
        path.Length <= MaxLength && !path.Any(char.IsControl) && Pattern().IsMatch(path);

    /// <summary>A Gmail system label name, which a topic label must not be.</summary>
    public static bool IsReserved(string path) => GmailLimits.ReservedLabelNames.Contains(path);

    [GeneratedRegex(@"^[^/\s][^/]{0,99}(/[^/\s][^/]{0,99}){0,4}$")]
    private static partial Regex Pattern();
}
