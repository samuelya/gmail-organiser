using System.Text.Json;
using GmailOrganiser.Data;
using GmailOrganiser.Memory;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// Reads one suggestion's optional <c>extraLabels</c> (#528, DESIGN §6.2). The model may only repeat extras of the
/// approved similar past decisions in the same prompt (<see cref="SuggestionParseContext.AllowedExtraLabels"/>), so a first
/// assessment, with none, never gets any. Pure; never fails the email.
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
    /// The kept extras: each string entry (trimmed, surrounding backticks removed) equal (case-insensitive) to an allowed
    /// extra, in the stored spelling, then <see cref="ExtraLabelList.Clean"/>ed against the topic, document type and
    /// configured labels. Missing, null or not an array is <c>[]</c>; non-string and blank entries are ignored; a string
    /// entry that is not allowed is reported in <paramref name="dropped"/>.
    /// </summary>
    public static string[] Read(
        string id, JsonElement item, string topic, string? documentType, SuggestionParseContext context, List<string> dropped)
    {
        if (!item.TryGetProperty(ExtraLabelList.Field, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var kept = new List<string>();
        var skipped = 0;
        foreach (var entry in element.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
        {
            var label = entry.GetString()!.Trim().Trim('`').Trim();
            if (label.Length == 0)
            {
                continue;
            }

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
            dropped.Add(SuggestionOutputParser.DroppedNote(id, ExtraLabelList.Field, skipped, "not among the similar past decisions' extras"));
        }

        return ExtraLabelList.Clean(kept, topic, documentType, context.ConfiguredLabels);
    }
}
