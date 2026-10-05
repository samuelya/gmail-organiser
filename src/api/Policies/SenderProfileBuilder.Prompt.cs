using System.Globalization;
using System.Text;
using GmailOrganiser.Data;
using GmailOrganiser.Senders;

namespace GmailOrganiser.Policies;

public sealed partial class SenderProfileBuilder
{
    /// <summary>The bound on <see cref="ToPromptText"/> without bodies.</summary>
    public const int PromptMaxChars = 2500;

    private const int MaxFieldChars = 60;
    private const int MaxSubjectChars = 50;

    /// <summary>
    /// The profile as the compact, deterministic text block the prompts embed: stats as <c>key: value</c>, one line
    /// per template, then the bodies. Every field is clipped, and templates that do not fit in
    /// <see cref="PromptMaxChars"/> fold into the <c>+N other subjects</c> line, so the text without bodies never
    /// exceeds it.
    /// </summary>
    public static string ToPromptText(SenderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var s = profile.Stats;
        var sb = new StringBuilder();
        Line(sb, "scope", $"{Snake(profile.Scope)} {Clip(profile.ScopeKey, MaxFieldChars)}");
        Line(sb, "names", string.Join(", ", profile.DisplayNames.Select(n => Clip(n, 30))));
        Line(sb, "addresses", string.Join(", ", profile.Addresses.Take(5).Select(a => Clip(a, 40)))
            + (profile.Addresses.Count > 5 ? $" (+{profile.Addresses.Count - 5})" : ""));
        Line(sb, "total", Num(s.Total));
        Line(sb, "unread_ratio", Num(s.UnreadRatio));
        Line(sb, "replied", Num(s.Replied));
        Line(sb, "starred", Num(s.Starred));
        Line(sb, "list_unsubscribe_ratio", Num(s.ListUnsubscribeRatio));
        Line(sb, "bulk_header_ratio", Num(s.BulkHeaderRatio));
        Line(sb, "categories", Mix(s.CategoryMix, int.MaxValue));
        Line(sb, "kind", Snake(s.Kind));
        Line(sb, "first_seen", Date(s.FirstSeen));
        Line(sb, "last_seen", Date(s.LastSeen));
        Line(sb, "allowlisted", s.Allowlisted ? "true" : "false");
        Line(sb, "labels_in_use", string.Join(", ", profile.LabelsInUse.Select(l => $"{Clip(l.Label, 40)} ({Num(l.Count)})")));
        Line(sb, "approved_policies_same_domain", string.Join("; ", profile.ApprovedPolicyHints.Select(Hint)));
        sb.Append("templates (count | template | newest subject | headers | categories | attachments | unread):\n");

        var lines = profile.Templates.Select(TemplateLine).ToList();
        var otherCount = profile.OtherTemplates;
        var otherMessages = profile.OtherTemplateMessages;
        while (lines.Count > 0 && sb.Length + lines.Sum(l => l.Length) + OtherLine(otherCount, otherMessages).Length > PromptMaxChars)
        {
            var dropped = profile.Templates[lines.Count - 1];
            otherCount++;
            otherMessages += dropped.Count;
            lines.RemoveAt(lines.Count - 1);
        }

        lines.ForEach(l => sb.Append(l));
        sb.Append(OtherLine(otherCount, otherMessages));

        foreach (var body in profile.Bodies)
        {
            sb.Append("body of \"").Append(Clip(body.Template, MaxFieldChars)).Append("\":\n").Append(body.Text).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string TemplateLine(SenderTemplate t)
    {
        var headers = string.Join(",", new[]
        {
            t.ListIdPresent ? "list-id" : null,
            t.ListUnsubscribePresent ? "unsubscribe" : null,
            t.Precedence is { } p ? "precedence=" + Clip(p, 12) : null,
        }.OfType<string>());
        return $"- {Num(t.Count)} | {Quote(t.Template)} | {Quote(t.ExampleSubject)} | {(headers.Length == 0 ? "-" : headers)} | "
            + $"{Mix(t.CategoryMix, 2)} | {Num(t.AttachmentRatio)} | {Num(t.UnreadRatio)}\n";
    }

    private static string OtherLine(int templates, int messages) =>
        templates == 0 ? "" : $"+{Num(templates)} other subjects ({Num(messages)} messages)\n";

    private static string Hint(PolicyHint h) =>
        $"{Snake(h.Scope)} {Clip(h.ScopeKey, 40)} -> {(h.IsMixed ? "mixed" : Clip(h.TopicLabel ?? "", 40))}"
        + (h.DocumentTypeLabel is { } d ? " + " + Clip(d, 30) : "")
        + " " + Snake(h.Action);

    /// <summary>The non-zero categories, most first, at most <paramref name="max"/>; <c>-</c> when none.</summary>
    private static string Mix(CategoryMix m, int max)
    {
        var parts = new (string Name, int Count)[]
            {
                ("primary", m.Primary), ("promotions", m.Promotions), ("social", m.Social), ("updates", m.Updates),
                ("forums", m.Forums), ("none", m.None),
            }
            .Where(p => p.Count > 0)
            .OrderByDescending(p => p.Count)
            .Take(max)
            .Select(p => $"{p.Name} {Num(p.Count)}");
        var text = string.Join(", ", parts);
        return text.Length == 0 ? "-" : text;
    }

    private static void Line(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(": ").Append(value.Length == 0 ? "-" : value).Append('\n');

    private static string Quote(string? value) =>
        string.IsNullOrEmpty(value) ? "(no subject)" : "\"" + Clip(value, MaxSubjectChars) + "\"";

    /// <summary>At most <paramref name="max"/> characters on one line, an ellipsis marking the cut.</summary>
    private static string Clip(string value, int max)
    {
        var flat = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= max)
        {
            return flat;
        }

        var cut = char.IsHighSurrogate(flat[max - 2]) ? max - 2 : max - 1;
        return flat[..cut] + "…";
    }

    private static string Snake<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToDb(value);

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Date(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
}
