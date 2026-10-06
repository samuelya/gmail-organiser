using System.Globalization;
using System.Text;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Senders;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Rules.Taxonomy;

/// <summary>One proposed area label: its full path, the description for the user and the sender keys it covers.</summary>
public sealed record TaxonomyLabel(string Path, string Description, IReadOnlyList<string> Senders);

/// <param name="Notes">The model's notes, then what the parser dropped and why; they become the plan's warnings.</param>
public sealed record ParsedTaxonomy(IReadOnlyList<TaxonomyLabel> Labels, IReadOnlyList<string> Notes);

/// <summary>
/// The taxonomy prompt (#366, DESIGN §6.5): one compact line per top sender's profile, the existing label tree and the
/// rules for area labels in, at most <c>maxLabels</c> labels with their senders out. Turns the answer into label plan
/// items. No I/O.
/// </summary>
public static class TaxonomyPrompt
{
    public const string Version = "taxonomy-v1";

    /// <summary>The template's first line; the fake LLM recognises the prompt by it.</summary>
    public const string Marker = "# Taxonomy proposal";

    /// <summary>Starts every profile line; the sender key follows up to the first <see cref="FieldSeparator"/>.</summary>
    public const string ProfilePrefix = "- sender: ";
    public const string FieldSeparator = " | ";

    public const string ProposalReason = "taxonomy proposal";
    public const double ProposalConfidence = 0.5;
    public const int MaxDepth = 2;
    public const int MaxDescriptionLength = 300;
    public const int MaxNotesLength = 1000;
    public const int MaxEditDistance = 2;
    public const int MaxLabelTreeEntries = 500;
    public const int MaxSubjects = 3;

    /// <summary>At most this many notes about dropped labels; the rest are counted in one note.</summary>
    public const int MaxDropNotes = 20;

    private const string ResourceName = "GmailOrganiser.Rules.Taxonomy.taxonomy-v1.md";
    private const int MaxFieldChars = 60;

    public static string Template { get; } = Load();

    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "labels": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "parent": { "type": ["string", "null"] },
                  "description": { "type": "string" },
                  "senders": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["name", "parent", "description", "senders"]
              }
            },
            "notes": { "type": "string" }
          },
          "required": ["labels", "notes"]
        }
        """).RootElement.Clone();

    /// <summary>Schema-constrained JSON at temperature 0, with <paramref name="numCtx"/> as <c>num_ctx</c> (#353).</summary>
    public static ChatOptions CreateOptions(int numCtx) => Llm.LlmCallMeter.NoThink(new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(OutputSchema, "taxonomy"),
        Temperature = 0,
        AdditionalProperties = new() { [Llm.LlmCallMeter.NumCtxKey] = numCtx },
    });

    /// <summary>
    /// The instructions up to the label tree are the system message; the label tree and the profiles (label names,
    /// sender names and subjects) are the user message.
    /// </summary>
    public static IList<ChatMessage> Build(
        IReadOnlyList<string> profileLines, IReadOnlyList<string> labelTree, string? documentTypeParent, int maxLabels)
    {
        ArgumentNullException.ThrowIfNull(profileLines);
        ArgumentNullException.ThrowIfNull(labelTree);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["maxLabels"] = maxLabels.ToString(CultureInfo.InvariantCulture),
            ["mailTypes"] = string.Join(", ", Enum.GetValues<MailType>().Select(t => SnakeCaseEnumConverter<MailType>.ToDb(t))),
            ["documentTypes"] = RenderDocumentTypes(documentTypeParent),
            ["labelTree"] = RenderLabelTree(labelTree),
            ["profiles"] = profileLines.Count == 0 ? "(no senders)" : string.Join('\n', profileLines),
        };
        var index = Template.IndexOf("{{labelTree}}", StringComparison.Ordinal);
        var lineStart = Template.LastIndexOf('\n', Template.LastIndexOf('\n', index) - 1) + 1;
        return
        [
            new ChatMessage(ChatRole.System, PromptTemplate.Substitute(Template[..lineStart], values).Trim()),
            new ChatMessage(ChatRole.User, PromptTemplate.Substitute(Template[lineStart..], values).Trim()),
        ];
    }

    /// <summary>
    /// The profile as one line: key, names, messages, kind, categories, labels in use and the top subjects. The key
    /// comes first, so no subject or name can stand in for it, and is never clipped: <see cref="Parse"/> accepts only
    /// the exact profiled key the model echoes.
    /// </summary>
    public static string ProfileLine(SenderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var s = profile.Stats;
        var m = s.CategoryMix;
        var categories = new (string Name, int Count)[]
            {
                ("primary", m.Primary), ("promotions", m.Promotions), ("social", m.Social), ("updates", m.Updates),
                ("forums", m.Forums), ("none", m.None),
            }
            .Where(c => c.Count > 0)
            .OrderByDescending(c => c.Count)
            .Take(2)
            .Select(c => $"{c.Name} {c.Count.ToString(CultureInfo.InvariantCulture)}");
        string[] fields =
        [
            profile.ScopeKey,
            string.Join(", ", profile.DisplayNames.Take(2).Select(n => Clip(n, 30))),
            s.Total.ToString(CultureInfo.InvariantCulture),
            SnakeCaseEnumConverter<SenderKind>.ToDb(s.Kind),
            string.Join(", ", categories),
            string.Join(", ", profile.LabelsInUse.Take(3).Select(l => $"{Clip(l.Label, 40)} ({l.Count.ToString(CultureInfo.InvariantCulture)})")),
            string.Join("; ", profile.Templates.Take(MaxSubjects).Select(t => "\"" + Clip(t.ExampleSubject ?? t.Template, 50) + "\"")),
        ];
        return ProfilePrefix + string.Join(FieldSeparator, fields.Select(f => f.Length == 0 ? "-" : f));
    }

    /// <summary>
    /// Reads <c>{ labels: [{ name, parent, description, senders }], notes }</c>. A label that is not a valid path, is
    /// more than <see cref="MaxDepth"/> levels deep, is (or is under) a protected or document-type label, repeats an
    /// earlier one or comes after <paramref name="maxLabels"/> is dropped with a note; so are sender keys the profiles
    /// do not list and senders already assigned. Null with <paramref name="error"/> when the answer is not that shape.
    /// </summary>
    /// <param name="senderKeys">The profiled sender keys (compared case-insensitively; the profile's spelling is kept).</param>
    public static ParsedTaxonomy? Parse(
        string? answer, IReadOnlyCollection<string> senderKeys, int maxLabels, IReadOnlyCollection<string> protectedNames,
        string? documentTypeParent, out string? error)
    {
        ArgumentNullException.ThrowIfNull(senderKeys);
        ArgumentNullException.ThrowIfNull(protectedNames);
        error = null;
        using var document = ReadObject(answer);
        if (document is null || !document.RootElement.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            error = "The model's answer is not a JSON object with a labels array.";
            return null;
        }

        var known = senderKeys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TaxonomyLabel>();
        var drops = new List<string>();
        var (unknown, repeated, overLimit) = (0, 0, 0);
        foreach (var item in labels.EnumerateArray())
        {
            if (Path(item) is not { } path)
            {
                drops.Add("A label without a name was dropped.");
                continue;
            }

            if (Refusal(path, protectedNames, documentTypeParent) is { } reason)
            {
                drops.Add($"\"{Clip(path, MaxFieldChars)}\" was dropped: {reason}.");
                continue;
            }

            if (!paths.Add(path))
            {
                drops.Add($"\"{Clip(path, MaxFieldChars)}\" was proposed twice; the first is kept.");
                continue;
            }

            if (result.Count >= maxLabels)
            {
                overLimit++;
                continue;
            }

            var senders = new List<string>();
            if (item.TryGetProperty("senders", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in list.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!.Trim()))
                {
                    if (!known.TryGetValue(key, out var canonical))
                    {
                        unknown++;
                    }
                    else if (!assigned.Add(canonical))
                    {
                        repeated++;
                    }
                    else
                    {
                        senders.Add(canonical);
                    }
                }
            }

            result.Add(new TaxonomyLabel(path, Clip(String(item, "description") ?? "", MaxDescriptionLength), senders));
        }

        var notes = new List<string>();
        if (document.RootElement.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Trim() is { Length: > 0 } text)
        {
            notes.Add(Clip(text, MaxNotesLength));
        }

        notes.AddRange(drops.Take(MaxDropNotes));
        if (drops.Count > MaxDropNotes)
        {
            notes.Add($"{drops.Count - MaxDropNotes} more labels were dropped.");
        }

        if (overLimit > 0)
        {
            notes.Add($"{overLimit} labels over the limit of {maxLabels} were dropped.");
        }

        if (unknown > 0)
        {
            notes.Add($"{unknown} sender keys that no profile lists were dropped.");
        }

        if (repeated > 0)
        {
            notes.Add($"{repeated} senders assigned to a second label kept only the first.");
        }

        return new ParsedTaxonomy(result, notes);
    }

    /// <summary>
    /// One plan item per label: a <see cref="LabelPlanItemKind.Create"/> (with the label's id when it exists already),
    /// or a taxonomy <see cref="LabelPlanItemKind.NearDuplicate"/> when the name is within edit distance
    /// <see cref="MaxEditDistance"/> of a non-protected user label (or <see cref="LabelPlanBuilder.AreNearDuplicates"/>),
    /// whose senders then go to that label.
    /// </summary>
    /// <param name="userLabels">The account's user labels in catalogue order.</param>
    /// <param name="senderTotals">Messages per sender key, for the item's message count.</param>
    public static IReadOnlyList<LabelPlanItem> Items(
        ParsedTaxonomy taxonomy, IReadOnlyList<GmailLabel> userLabels, IReadOnlyCollection<string> protectedNames,
        IReadOnlyDictionary<string, int> senderTotals)
    {
        ArgumentNullException.ThrowIfNull(taxonomy);
        ArgumentNullException.ThrowIfNull(userLabels);
        var candidates = userLabels.Where(l => !protectedNames.Contains(l.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var items = new List<LabelPlanItem>();
        foreach (var label in taxonomy.Labels)
        {
            var total = label.Senders.Sum(s => (long)senderTotals.GetValueOrDefault(s));
            var counted = string.Create(CultureInfo.InvariantCulture, $"{label.Senders.Count} senders ({total} messages)");
            var item = new LabelPlanItem(
                Guid.NewGuid(), LabelPlanItemKind.Create, "", label.Path, total, null, null, null, [], "",
                LabelPlanItemStatus.Proposed, Description: label.Description, SenderKeys: label.Senders);
            if (userLabels.FirstOrDefault(l => string.Equals(l.Name, label.Path, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                items.Add(item with { LabelId = same.Id, LabelName = same.Name, Rationale = $"An existing label; its {counted} get a proposed policy." });
            }
            else if (candidates
                .Select(l => (Label: l, Distance: Distance(label.Path, l.Name)))
                .Where(c => c.Distance <= MaxEditDistance || LabelPlanBuilder.AreNearDuplicates(label.Path, c.Label.Name))
                .OrderBy(c => c.Distance)
                .Select(c => c.Label)
                .FirstOrDefault() is { } near)
            {
                items.Add(item with
                {
                    Kind = LabelPlanItemKind.NearDuplicate,
                    TargetLabelId = near.Id,
                    TargetLabelName = near.Name,
                    Rationale = $"The proposed name nearly duplicates \"{near.Name}\", so its {counted} go there instead of a new label.",
                });
            }
            else
            {
                items.Add(item with { Rationale = $"A new area label for {counted}." });
            }
        }

        return items;
    }

    /// <summary>
    /// The edit distance of the normalised names (<see cref="LabelPlanBuilder.Normalise"/>), or <see cref="int.MaxValue"/>
    /// when either is shorter than <see cref="LabelPlanBuilder.FuzzyMinLength"/>, where two edits make another word.
    /// </summary>
    public static int Distance(string a, string b)
    {
        var (x, y) = (LabelPlanBuilder.Normalise(a), LabelPlanBuilder.Normalise(b));
        if (x.Length < LabelPlanBuilder.FuzzyMinLength || y.Length < LabelPlanBuilder.FuzzyMinLength)
        {
            return x == y ? 0 : int.MaxValue;
        }

        var previous = Enumerable.Range(0, y.Length + 1).ToArray();
        var current = new int[y.Length + 1];
        for (var i = 1; i <= x.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[y.Length];
    }

    /// <summary>The label's full path with trimmed segments (<c>parent/name</c>, or <c>name</c> when it has no parent); null without a name.</summary>
    private static string? Path(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || String(item, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        var full = String(item, "parent") is { Length: > 0 } parent ? parent + "/" + name : name;
        return string.Join('/', full.Split('/').Select(s => s.Trim()));
    }

    private static string? Refusal(string path, IReadOnlyCollection<string> protectedNames, string? documentTypeParent)
    {
        if (path.Split('/').Length > MaxDepth)
        {
            return $"more than {MaxDepth} levels";
        }

        if (!LabelResolver.IsValid(path))
        {
            return "not a valid label name";
        }

        if (protectedNames.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return "the app or the Apps Script uses this label";
        }

        return documentTypeParent is { Length: > 0 } d
            && (string.Equals(path, d.Trim(), StringComparison.OrdinalIgnoreCase) || path.StartsWith(d.Trim() + "/", StringComparison.OrdinalIgnoreCase))
            ? "document types are not area labels"
            : null;
    }

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;

    /// <summary>The first JSON object in the answer (after any prose or code fence); trailing text is ignored.</summary>
    private static JsonDocument? ReadObject(string? answer)
    {
        var start = answer?.IndexOf('{', StringComparison.Ordinal) ?? -1;
        if (start < 0)
        {
            return null;
        }

        var reader = new Utf8JsonReader(
            Encoding.UTF8.GetBytes(answer![start..]),
            new JsonReaderOptions { AllowTrailingCommas = true, AllowMultipleValues = true, CommentHandling = JsonCommentHandling.Skip });
        try
        {
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RenderLabelTree(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
        {
            return "(no labels yet)";
        }

        var lines = labels.Take(MaxLabelTreeEntries).Select(l => Clip(l, 200)).ToList();
        if (labels.Count > MaxLabelTreeEntries)
        {
            lines.Add(AnalysisPromptBuilder.LabelsOmitted);
        }

        return string.Join('\n', lines);
    }

    private static string RenderDocumentTypes(string? parent) =>
        string.IsNullOrWhiteSpace(parent)
            ? "Document-type labels are switched off."
            : $"Document types (statements, invoices, contracts) are labelled under `{Clip(parent.Trim(), MaxFieldChars)}`.";

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

    private static string Load()
    {
        using var stream = typeof(TaxonomyPrompt).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded prompt template '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
