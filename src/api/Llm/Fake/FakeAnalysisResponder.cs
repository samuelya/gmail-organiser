using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GmailOrganiser.Analysis.Prompts;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Llm.Fake;

/// <summary>
/// The deterministic answer of <c>LLM_FAKE=true</c>: a schema-valid suggestion per email of an analysis prompt, derived
/// from the email headers only. Any other prompt (model test, vision) gets <see cref="FixedAnswer"/>.
/// </summary>
public static class FakeAnalysisResponder
{
    public const string FixedAnswer = """{"ok":true}""";
    public const string LabelTreeHeading = "Existing label tree (one path per line):";
    public const double MinConfidence = 0.6;
    public const double MaxConfidence = 0.95;

    private const string EmailsHeading = "Emails to classify (";
    private const string EmailHeading = "### Email ";
    private static readonly string[] BillWords = ["invoice", "bill", "payment"];

    public static string Answer(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var lines = string.Join('\n', messages.Select(m => m.Text)).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var emails = ReadEmails(lines);
        if (emails.Count == 0)
        {
            return FixedAnswer;
        }

        var tree = ReadLabelTree(lines);
        var suggestions = emails.Select(e => Suggest(e, tree)).ToList();
        return JsonSerializer.Serialize(new { suggestions });
    }

    /// <summary>A value in [<see cref="MinConfidence"/>, <see cref="MaxConfidence"/>] from a SHA-256 of the id.</summary>
    public static double Confidence(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        var steps = (int)Math.Round((MaxConfidence - MinConfidence) * 100);
        return Math.Round(MinConfidence + (BitConverter.ToUInt32(hash, 0) % (steps + 1)) / 100.0, 2);
    }

    private static object Suggest(FakeEmail e, HashSet<string> tree)
    {
        var category = (e.Category ?? "-").Trim();
        var subject = e.Subject ?? "";
        var domain = DomainLabel(e.From);
        var promotions = category.Equals("promotions", StringComparison.OrdinalIgnoreCase);
        var social = category.Equals("social", StringComparison.OrdinalIgnoreCase);
        var bill = !promotions && !social && BillWords.Any(w => subject.Contains(w, StringComparison.OrdinalIgnoreCase));
        var label = promotions ? "Promotions" : social ? "Social" : bill ? $"Bills/{domain}" : $"Updates/{domain}";
        string[] replace = e.CurrentLabels.Count > 0 && subject.Contains("bill", StringComparison.OrdinalIgnoreCase)
            ? [e.CurrentLabels[0]]
            : [];

        return new
        {
            id = e.Id,
            topicLabel = label,
            isNewLabel = !tree.Contains(label),
            needsAction = bill,
            toBeDeleted = promotions,
            unsubscribeSuggested = promotions,
            confidence = Confidence(e.Id),
            reason = $"Fake answer for the {category} email",
            replaceLabels = replace,
        };
    }

    // Reads at most the announced number of email blocks and skips every body, so email or attachment text can never
    // add an id or change a field.
    private static List<FakeEmail> ReadEmails(List<string> lines)
    {
        var emails = new List<FakeEmail>();
        var start = lines.FindIndex(l => l.StartsWith(EmailsHeading, StringComparison.Ordinal));
        if (start < 0 || !int.TryParse(lines[start][EmailsHeading.Length..].TrimEnd(':', ')'), CultureInfo.InvariantCulture, out var count))
        {
            return emails;
        }

        FakeEmail? email = null;
        for (var i = start + 1; i < lines.Count && emails.Count <= count; i++)
        {
            var line = lines[i];
            if (line.Trim() == AnalysisPromptBuilder.BodyStart)
            {
                email = null;
                while (i < lines.Count && lines[i].Trim() != AnalysisPromptBuilder.BodyEnd)
                {
                    i++;
                }
            }
            else if (line.StartsWith(EmailHeading, StringComparison.Ordinal) && emails.Count < count
                && i + 1 < lines.Count && Field(lines[i + 1], "id") is { Length: > 0 } id)
            {
                email = new FakeEmail(id);
                emails.Add(email);
                i++;
            }
            else if (email is not null)
            {
                email.From ??= Field(line, "from");
                email.Category ??= Field(line, "category");
                email.Subject ??= Field(line, "subject");
                if (Field(line, "current labels") is { } current && current != "-")
                {
                    email.CurrentLabels.AddRange(current.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }

        return emails;
    }

    private static HashSet<string> ReadLabelTree(List<string> lines)
    {
        var tree = new HashSet<string>(StringComparer.Ordinal);
        var start = lines.FindIndex(l => l.Trim() == LabelTreeHeading);
        for (var i = start + 1; start >= 0 && i < lines.Count && lines[i].Trim().Length > 0; i++)
        {
            tree.Add(lines[i].Trim());
        }

        return tree;
    }

    private static string? Field(string line, string name) =>
        line.StartsWith(name + ":", StringComparison.Ordinal) ? line[(name.Length + 1)..].Trim() : null;

    /// <summary>The second-level label of the sender's domain, capitalised: <c>a@mail.shop.example.com</c> → <c>Example</c>.</summary>
    private static string DomainLabel(string? from)
    {
        var at = from?.LastIndexOf('@') ?? -1;
        var domain = at < 0 ? "" : from![(at + 1)..].Trim().TrimEnd('>').Trim();
        var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var label = parts.Length >= 2 ? parts[^2] : parts.FirstOrDefault() ?? "";
        return label.Length == 0 ? "Other" : char.ToUpper(label[0], CultureInfo.InvariantCulture) + label[1..].ToLowerInvariant();
    }

    private sealed class FakeEmail(string id)
    {
        public string Id { get; } = id;
        public string? From { get; set; }
        public string? Category { get; set; }
        public string? Subject { get; set; }
        public List<string> CurrentLabels { get; } = [];
    }
}
