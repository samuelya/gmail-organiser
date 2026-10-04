using System.Text.RegularExpressions;

namespace GmailOrganiser.Gmail;

/// <summary>The topic label rule shared by the LLM output parser and the review edit.</summary>
public static partial class LabelPath
{
    /// <summary>Up to <see cref="GmailLimits.LabelMaxSegments"/> <c>/</c>-separated segments, none blank or starting with whitespace, at most <see cref="GmailLimits.LabelNameMaxLength"/> chars.</summary>
    public static bool IsValid(string path) =>
        path.Length <= GmailLimits.LabelNameMaxLength && !path.Any(char.IsControl) && Pattern().IsMatch(path);

    /// <summary>A Gmail system label name, which a topic label must not be.</summary>
    public static bool IsReserved(string path) => GmailLimits.ReservedLabelNames.Contains(path);

    [GeneratedRegex(@"^[^/\s][^/]{0,99}(/[^/\s][^/]{0,99}){0,4}$")]
    private static partial Regex Pattern();
}
