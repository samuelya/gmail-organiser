using System.Globalization;
using System.Text.Json;
using GmailOrganiser.Rules.Taxonomy;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Llm.Fake;

/// <summary>
/// The deterministic answer of <c>LLM_FAKE=true</c> to the taxonomy prompt (#366): one top-level label per domain
/// part left of the last two (<c>news@mail.shop.example.com</c> → <c>Shop</c>, <c>a@example.com</c> → <c>Example</c>) of
/// the profiled senders, in order of first appearance, with those senders. Reads only each profile line's key, which
/// comes before any name or subject.
/// </summary>
public static class FakeTaxonomyResponder
{
    /// <summary>The answer to a taxonomy prompt; null for any other prompt.</summary>
    public static string? Answer(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0 || !messages[0].Text.TrimStart().StartsWith(TaxonomyPrompt.Marker + "\n", StringComparison.Ordinal))
        {
            return null;
        }

        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in messages.SelectMany(m => m.Text.Split('\n')).Where(l => l.StartsWith(TaxonomyPrompt.ProfilePrefix, StringComparison.Ordinal)))
        {
            var rest = line[TaxonomyPrompt.ProfilePrefix.Length..];
            var key = rest[..(rest.IndexOf(TaxonomyPrompt.FieldSeparator, StringComparison.Ordinal) is var end and >= 0 ? end : rest.Length)].Trim();
            var label = DomainLabel(key);
            if (!groups.TryGetValue(label, out var senders))
            {
                groups[label] = senders = [];
            }

            senders.Add(key);
        }

        var labels = groups.Select(g => new
        {
            name = g.Key,
            parent = (string?)null,
            description = $"Mail from {g.Key.ToLowerInvariant()}",
            senders = g.Value,
        });
        return JsonSerializer.Serialize(new { labels, notes = "Fake answer: one label per sender domain." });
    }

    private static string DomainLabel(string key)
    {
        var parts = key[(key.LastIndexOf('@') + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var label = parts.Length >= 3 ? parts[^3] : parts.FirstOrDefault() ?? "";
        return label.Length == 0 ? "Senders" : char.ToUpper(label[0], CultureInfo.InvariantCulture) + label[1..].ToLowerInvariant();
    }
}
