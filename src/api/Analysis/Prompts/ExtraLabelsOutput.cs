using System.Text.Json;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// Reads one suggestion's optional <c>extraLabels</c> (#528, DESIGN §6.2). The model may only repeat extras of the
/// similar past decisions in the same prompt (<see cref="SuggestionParseContext.AllowedExtraLabels"/>), so a first
/// assessment, with none, never gets any. Pure; never fails the email.
/// </summary>
public static class ExtraLabelsOutput
{
    /// <summary>
    /// The kept extras: each string entry equal (case-insensitive) to an allowed extra, in the allowed spelling, then
    /// <see cref="ExtraLabelList.Clean"/>ed against the topic, document type and configured labels. Missing, null or not an
    /// array is <c>[]</c>; non-string entries are ignored; a string entry that is not allowed is reported in
    /// <paramref name="dropped"/>.
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
            var label = entry.GetString()!.Trim();
            var allowed = context.AllowedExtraLabels.FirstOrDefault(a => string.Equals(a.Trim(), label, StringComparison.OrdinalIgnoreCase));
            if (allowed is null)
            {
                skipped++;
            }
            else
            {
                kept.Add(allowed.Trim());
            }
        }

        if (skipped > 0)
        {
            dropped.Add($"Email '{id}': {skipped} '{ExtraLabelList.Field}' entr{(skipped == 1 ? "y" : "ies")} not among the similar past decisions' extras; dropped.");
        }

        return ExtraLabelList.Clean(kept, topic, documentType, context.ConfiguredLabels);
    }
}
