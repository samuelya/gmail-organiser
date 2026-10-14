using System.Text.Json;
using GmailOrganiser.Data;
using GmailOrganiser.Memory;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// Reads one suggestion's optional <c>extraLabels</c> (#528, DESIGN §6.2). The model may only repeat extras of the
/// approved similar past decisions in the same prompt, or existing labels the person's comment names (#582)
/// (<see cref="SuggestionParseContext.AllowedExtraLabels"/>), so a first assessment without a comment never gets any.
/// Pure; never fails the email.
/// </summary>
public static class ExtraLabelsOutput
{
    private static readonly string Approved = SnakeCaseEnumConverter<DecisionOutcome>.ToDb(DecisionOutcome.Approved);

    /// <summary>
    /// The extras a hint shows: only an approved decision's (a rejected one's extras were turned down), without blanks.
    /// </summary>
    public static IEnumerable<string> Shown(MemoryHint hint) =>
        string.Equals(hint.Outcome, Approved, StringComparison.OrdinalIgnoreCase)
            ? hint.ExtraLabels.Where(l => ShownForm(l).Length > 0)
            : [];

    /// <summary>An extra as the prompt shows it, between backticks: one line, body tags defused.</summary>
    public static string ShownForm(string label) => AnalysisPromptBuilder.OneLine(label);

    /// <summary>
    /// The labels of <paramref name="labelTree"/> whose whole name (trimmed) appears in <paramref name="comment"/>,
    /// case-insensitive, not inside a longer word, label path or name (<c>-</c>, <c>_</c>, <c>.</c> and <c>/</c> between
    /// letters or digits join a name): a comment asking for <c>Topic/Sub</c> names neither <c>Topic</c> nor <c>Sub</c>,
    /// and <c>Archive-2023</c> does not name <c>Archive</c>. Only existing labels, so a comment never invents one. The
    /// list is what the comment mentions, not what it asks for: a label named in a negative sense ("not Archive") is
    /// allowed too, and leaving it out is the model's reading of the comment.
    /// </summary>
    public static IEnumerable<string> NamedIn(string comment, IEnumerable<string> labelTree) =>
        labelTree.Where(l => l.Trim().Length > 0 && Names(comment, l.Trim()));

    private static bool Names(string comment, string label)
    {
        for (var at = comment.IndexOf(label, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = comment.IndexOf(label, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + label.Length;
            if (!Joined(comment, at - 1, -1) && !Joined(comment, end, 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="text"/>[<paramref name="at"/>] continues the name: a letter or digit, or a joiner
    /// (<c>/</c>, <c>-</c>, <c>_</c>, <c>.</c>) followed (in <paramref name="step"/>'s direction) by one, so a sentence's
    /// full stop or a dash between words still ends a name.
    /// </summary>
    private static bool Joined(string text, int at, int step) =>
        at >= 0 && at < text.Length
        && (char.IsLetterOrDigit(text[at])
            || (text[at] is '/' or '-' or '_' or '.' && at + step >= 0 && at + step < text.Length && char.IsLetterOrDigit(text[at + step])));

    /// <summary>
    /// The allow-list for <paramref name="labels"/>: shown form (case-insensitive, first spelling wins) to the stored
    /// spelling, so an extra copied exactly as shown is kept as the decision stored it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Allowed(IEnumerable<string> labels)
    {
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels)
        {
            var shown = ShownForm(label);
            if (shown.Length > 0)
            {
                allowed.TryAdd(shown, label.Trim());
            }
        }

        return allowed;
    }

    /// <summary>
    /// The string entries of <paramref name="item"/>'s <c>extraLabels</c>, trimmed and without surrounding backticks;
    /// blanks, non-strings and a missing or non-array value give none.
    /// </summary>
    public static string[] Answered(JsonElement item) =>
        item.TryGetProperty(ExtraLabelList.Field, out var element) && element.ValueKind == JsonValueKind.Array
            ? [.. element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim().Trim('`').Trim()).Where(l => l.Length > 0)]
            : [];

    /// <summary>
    /// The kept extras: each string entry (trimmed, surrounding backticks removed) equal (case-insensitive) to an allowed
    /// extra, in the stored spelling, then <see cref="ExtraLabelList.Clean"/>ed against the topic, document type and
    /// configured labels. Missing, null or not an array is <c>[]</c>; non-string and blank entries are ignored; a string
    /// entry that is not allowed is reported in <paramref name="dropped"/>.
    /// </summary>
    public static string[] Read(
        string id, JsonElement item, string topic, string? documentType, SuggestionParseContext context, List<string> dropped)
    {
        var kept = new List<string>();
        var skipped = 0;
        foreach (var label in Answered(item))
        {
            if (context.AllowedExtraLabels.TryGetValue(label, out var allowed))
            {
                kept.Add(allowed);
            }
            else
            {
                skipped++;
            }
        }

        if (skipped > 0)
        {
            dropped.Add(SuggestionOutputParser.DroppedNote(id, ExtraLabelList.Field, skipped, "neither a similar past decision's extra nor an existing label the comment names"));
        }

        return ExtraLabelList.Clean(kept, topic, documentType, context.ConfiguredLabels);
    }
}
