using System.Text;
using System.Text.Json;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// Validates the model's JSON answer. Never throws on model output: anything unusable becomes an error string
/// (ids and field names only, never email content) and the caller records it as a failure.
/// </summary>
public static class SuggestionOutputParser
{
    public const int MaxLabelPathLength = GmailLimits.LabelNameMaxLength;
    public const int MaxReasonLength = 300;
    public const int MaxFilterValueLength = 200;
    public const double ConfidenceTolerance = 0.01;

    private const int MaxIdInError = 64;
    private const int MaxCandidates = 32;
    private const string InvalidText = "Item contains a string that is not valid UTF-16 text.";

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = true,
        AllowMultipleValues = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <param name="currentLabels">Each email's current personal label names, the only entries <c>replaceLabels</c>
    /// may hold; an email missing here has none.</param>
    /// <param name="documentTypeParent">The document-type parent label; null turns <c>documentTypeLabel</c> off.</param>
    /// <param name="context">The label tree and configured labels; <see cref="SuggestionParseContext.Default"/> when null.</param>
    public static ParsedSuggestions Parse(
        string? raw, IReadOnlySet<string> expectedIds, IReadOnlyDictionary<string, IReadOnlyList<string>>? currentLabels = null,
        string? documentTypeParent = null, SuggestionParseContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);
        var valid = new List<SuggestionOutput>();
        var errors = new List<string>();
        var dropped = new List<string>();
        var parent = string.IsNullOrWhiteSpace(documentTypeParent) ? null : documentTypeParent.Trim();
        var read = new ItemReader(
            context ?? SuggestionParseContext.Default, currentLabels ?? new Dictionary<string, IReadOnlyList<string>>(), parent, errors, dropped);
        FilterCriteriaOutput? filter = null;

        var document = ReadFirstValue(raw ?? string.Empty, out var candidates);
        if (document is null)
        {
            errors.Add(candidates == 0 ? "Output contains no JSON array or object." : "Output is not valid JSON.");
            return Finish(valid, errors, filter, expectedIds, [], read);
        }

        var answered = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        using (document)
        {
            var root = document.RootElement;
            IEnumerable<JsonElement> items;
            try
            {
                (items, var wrapped) = Items(root);
                filter = wrapped ? ReadFilter(root, errors) : null;
            }
            catch (InvalidOperationException)
            {
                // A lone surrogate escape (\ud800) in a name or string: System.Text.Json throws on reading it.
                errors.Add(InvalidText);
                return Finish(valid, errors, null, expectedIds, [], read);
            }

            foreach (var item in items)
            {
                try
                {
                    filter = ReadItem(item, expectedIds, read, filter, answered, accepted, valid);
                }
                catch (InvalidOperationException)
                {
                    errors.Add(InvalidText);
                }
            }
        }

        return Finish(valid, errors, filter, expectedIds, answered, read);
    }

    private static FilterCriteriaOutput? ReadItem(JsonElement item, IReadOnlySet<string> expectedIds, ItemReader read,
        FilterCriteriaOutput? filter, HashSet<string> answered, HashSet<string> accepted, List<SuggestionOutput> valid)
    {
        var errors = read.Errors;
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Array item is not an object.");
            return filter;
        }

        filter ??= ReadFilter(item, errors);
        if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() is not { Length: > 0 } id)
        {
            errors.Add("Item has no string 'id'.");
            return filter;
        }

        if (!expectedIds.Contains(id))
        {
            errors.Add($"Unknown id '{Shorten(id)}'.");
            return filter;
        }

        answered.Add(id);
        if (accepted.Contains(id))
        {
            errors.Add($"Duplicate id '{id}': first valid answer kept.");
        }
        else if (ReadSuggestion(id, item, read) is { } suggestion)
        {
            accepted.Add(id);
            valid.Add(suggestion);
        }

        return filter;
    }

    /// <summary>
    /// Reads one JSON value from the first <c>[</c>/<c>{</c> that starts one, ignoring whatever follows it (code
    /// fences, trailing prose). A candidate that parses but holds no object (<c>[3]</c> in leading prose) is passed
    /// over for a later one. Null when no candidate is valid JSON.
    /// </summary>
    internal static JsonDocument? ReadFirstValue(string raw, out int candidates)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        JsonDocument? fallback = null;
        candidates = 0;
        for (var i = 0; i < bytes.Length && candidates < MaxCandidates; i++)
        {
            if (bytes[i] is not ((byte)'[' or (byte)'{'))
            {
                continue;
            }

            candidates++;
            var reader = new Utf8JsonReader(bytes.AsSpan(i), ReaderOptions);
            JsonDocument document;
            try
            {
                document = JsonDocument.ParseValue(ref reader);
            }
            catch (JsonException)
            {
                continue;
            }

            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object || root.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object))
            {
                fallback?.Dispose();
                return document;
            }

            if (fallback is null)
            {
                fallback = document;
            }
            else
            {
                document.Dispose();
            }
        }

        return fallback;
    }

    /// <summary>
    /// <c>{"suggestions": [...]}</c> as asked, an array, a bare object for one email, or an object wrapping the array
    /// in a single property.
    /// </summary>
    private static (IEnumerable<JsonElement> Items, bool Wrapped) Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return (root.EnumerateArray(), false);
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("suggestions", out var suggestions)
            && suggestions.ValueKind == JsonValueKind.Array)
        {
            return (suggestions.EnumerateArray(), true);
        }

        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("id", out _))
        {
            return ([root], false);
        }

        var arrays = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).ToList();
        return arrays.Count == 1 ? (arrays[0].Value.EnumerateArray(), true) : ([root], false);
    }

    /// <summary>
    /// One email's answer. A usable <c>proposedNewLabel</c> becomes the topic label; <c>isNewLabel</c> is computed from
    /// the label tree (a disagreeing model value is only counted); an unknown or missing <c>mailType</c> is null with a note.
    /// </summary>
    private static SuggestionOutput? ReadSuggestion(string id, JsonElement item, ItemReader read)
    {
        var (errors, dropped, parent) = (read.Errors, read.Dropped, read.DocumentTypeParent);
        var current = read.Current.GetValueOrDefault(id) ?? [];
        var count = errors.Count;
        var proposed = ReadProposedNewLabel(id, item, read);
        var label = proposed ?? ReadString(item, "topicLabel")?.Trim();
        if (label is null || !IsValidLabelPath(label))
        {
            errors.Add($"Email '{id}': 'topicLabel' is missing or not a valid label path.");
        }
        else if (LabelPath.IsReserved(label))
        {
            errors.Add($"Email '{id}': 'topicLabel' is a Gmail system label.");
        }

        var isNewLabel = label is not null && !read.Context.LabelTree.Contains(label);
        var needsAction = ReadBool(id, item, "needsAction", errors);
        var toBeDeleted = ReadBool(id, item, "toBeDeleted", errors);
        var unsubscribe = ReadBool(id, item, "unsubscribeSuggested", errors);
        var confidence = ReadConfidence(id, item, errors);
        var reason = ReadString(item, "reason")?.Trim();
        if (reason is null)
        {
            errors.Add($"Email '{id}': 'reason' must be a string.");
        }

        var replaceLabels = ReadReplaceLabels(id, item, label, current, dropped);
        if (errors.Count > count)
        {
            return null;
        }

        if (item.TryGetProperty("isNewLabel", out var claimed) && claimed.ValueKind is JsonValueKind.True or JsonValueKind.False
            && claimed.GetBoolean() != isNewLabel)
        {
            read.NewLabelDisagreements++;
        }

        return new SuggestionOutput(id, label!, isNewLabel, needsAction, toBeDeleted, unsubscribe, confidence,
            Cut(reason!, MaxReasonLength), ReadDocumentTypeLabel(id, item, label!, parent, dropped), ReadMailType(id, item, read))
        {
            ReplaceLabels = replaceLabels,
            ProposedNewLabel = proposed,
        };
    }

    /// <summary>
    /// The optional <c>proposedNewLabel</c>, in the label tree's spelling of its longest existing prefix. Absent, null or
    /// blank is null; a value that is not a valid label path, a Gmail system label, or the app's action or delete label (or
    /// a label under one) is dropped with a note, and <c>topicLabel</c> stands.
    /// </summary>
    private static string? ReadProposedNewLabel(string id, JsonElement item, ItemReader read)
    {
        if (!item.TryGetProperty("proposedNewLabel", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var value = element.ValueKind == JsonValueKind.String ? element.GetString()!.Trim() : null;
        if (value is { Length: 0 })
        {
            return null;
        }

        if (value is null || !IsValidLabelPath(value) || LabelPath.IsReserved(value) || read.Context.ConfiguredLabels.Any(c => IsSameOrUnder(value, c)))
        {
            read.Dropped.Add($"Email '{id}': 'proposedNewLabel' ignored (not a usable new label).");
            return null;
        }

        return read.Context.LabelTree.Respell(value);
    }

    private static bool IsSameOrUnder(string label, string configured)
    {
        var name = configured.Trim();
        return name.Length > 0 && (string.Equals(label, name, StringComparison.OrdinalIgnoreCase)
            || label.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static MailType? ReadMailType(string id, JsonElement item, ItemReader read)
    {
        if (!read.Context.ReadMailType)
        {
            return null;
        }

        if (item.TryGetProperty("mailType", out var element) && element.ValueKind == JsonValueKind.String
            && SnakeCaseEnumConverter<MailType>.TryFromDb(element.GetString()!, out var type))
        {
            return type;
        }

        read.Dropped.Add($"Email '{id}': 'mailType' is missing or not one of {SnakeCaseEnumConverter<MailType>.NamesList}; none stored.");
        return null;
    }

    /// <summary>
    /// The optional <c>replaceLabels</c> array: entries naming one of the email's <paramref name="current"/> labels
    /// (case-insensitive, returned as the current label is written), other than the topic label; duplicates dropped.
    /// Anything else (not a string, a label the email does not carry, the app's action or delete label, which are never
    /// current labels, or the topic label itself) is dropped with a note in <paramref name="dropped"/>, never an
    /// error: small models echo the kept label here, and that must not cost the suggestion. Missing or null is empty.
    /// </summary>
    private static IReadOnlyList<string> ReadReplaceLabels(
        string id, JsonElement item, string? topicLabel, IReadOnlyList<string> current, List<string> dropped)
    {
        if (!item.TryGetProperty("replaceLabels", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            dropped.Add($"Email '{id}': 'replaceLabels' is not an array; ignored.");
            return [];
        }

        var labels = new List<string>();
        var skipped = 0;
        foreach (var entry in element.EnumerateArray())
        {
            var value = entry.ValueKind == JsonValueKind.String ? entry.GetString()?.Trim() : null;
            var match = value is null ? null : current.FirstOrDefault(c => string.Equals(c.Trim(), value, StringComparison.OrdinalIgnoreCase));
            if (match is null || string.Equals(value, topicLabel, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
            }
            else if (!labels.Contains(match, StringComparer.Ordinal))
            {
                labels.Add(match);
            }
        }

        if (skipped > 0)
        {
            dropped.Add($"Email '{id}': {skipped} 'replaceLabels' entr{(skipped == 1 ? "y" : "ies")} not among its replaceable current labels; dropped.");
        }

        return labels.Count == 0 ? [] : labels;
    }

    /// <summary>
    /// The optional <c>documentTypeLabel</c> by <see cref="DocumentTypePath"/>: 1 to <see cref="DocumentTypePath.MaxDepth"/>
    /// levels under <paramref name="parent"/> (case-insensitive, returned with the parent as configured), not the topic label. Absent, null or blank is null, as
    /// is anything with the parent off; any other value is dropped with a note in <paramref name="dropped"/> and the
    /// email's suggestion stays valid.
    /// </summary>
    private static string? ReadDocumentTypeLabel(string id, JsonElement item, string topicLabel, string? parent, List<string> dropped)
    {
        if (parent is null || !item.TryGetProperty("documentTypeLabel", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return Ignored("not a string");
        }

        var value = element.GetString()!.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        return DocumentTypePath.Normalise(value, parent, topicLabel, out var error) ?? Ignored(error switch
        {
            DocumentTypePathError.InvalidPath => "not a valid label path",
            DocumentTypePathError.NotUnderParent => $"not {DocumentTypePath.LevelsUnder(parent)} below the document-type parent",
            _ => "same as topicLabel",
        });

        string? Ignored(string reason)
        {
            dropped.Add($"Email '{id}': 'documentTypeLabel' ignored ({reason}).");
            return null;
        }
    }

    /// <summary>
    /// <paramref name="value"/> (trimmed) as stored for a suggestion: a valid label path 1 to <see cref="DocumentTypePath.MaxDepth"/> levels
    /// under <paramref name="parent"/> (case-insensitive), spelled with the parent as configured; null otherwise. Memory answers
    /// with the same spelling as the model's rows, so both group together.
    /// </summary>
    public static string? DocumentTypeUnder(string parent, string value)
    {
        return DocumentTypePath.Normalise(value.Trim(), parent, topicLabel: null, out _);
    }

    /// <inheritdoc cref="LabelPath.IsValid"/>
    public static bool IsValidLabelPath(string path) => LabelPath.IsValid(path);

    private static double ReadConfidence(string id, JsonElement item, List<string> errors)
    {
        if (item.TryGetProperty("confidence", out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var value) && double.IsFinite(value)
            && value >= -ConfidenceTolerance && value <= 1 + ConfidenceTolerance)
        {
            return Math.Clamp(value, 0, 1);
        }

        errors.Add($"Email '{id}': 'confidence' must be a number between 0 and 1.");
        return 0;
    }

    private static bool ReadBool(string id, JsonElement item, string name, List<string> errors)
    {
        if (item.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        errors.Add($"Email '{id}': '{name}' must be true or false.");
        return false;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static FilterCriteriaOutput? ReadFilter(JsonElement item, List<string> errors)
    {
        if (!item.TryGetProperty("filterCriteria", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("'filterCriteria' is not an object.");
            return null;
        }

        string[] names = ["from", "listId", "subjectContains"];
        if (names.All(n => !element.TryGetProperty(n, out var v) || v.ValueKind == JsonValueKind.Null))
        {
            return null;
        }

        string? Field(string name)
        {
            var value = ReadString(element, name)?.Trim();
            return value is { Length: > 0 and <= MaxFilterValueLength } && !value.Any(char.IsControl) ? value : null;
        }

        var filter = new FilterCriteriaOutput(Field(names[0]), Field(names[1]), Field(names[2]));
        if (filter is { From: null, ListId: null, SubjectContains: null })
        {
            errors.Add("'filterCriteria' has no usable field.");
            return null;
        }

        return filter;
    }

    private static ParsedSuggestions Finish(List<SuggestionOutput> valid, List<string> errors, FilterCriteriaOutput? filter,
        IReadOnlySet<string> expectedIds, HashSet<string> answered, ItemReader read)
    {
        errors.AddRange(expectedIds.Where(id => !answered.Contains(id)).Order(StringComparer.Ordinal).Select(id => $"Email '{id}': no answer."));
        return new ParsedSuggestions(valid, errors, filter) { Dropped = read.Dropped, NewLabelDisagreements = read.NewLabelDisagreements };
    }

    /// <summary>One parse's inputs and the notes it collects.</summary>
    private sealed class ItemReader(
        SuggestionParseContext context, IReadOnlyDictionary<string, IReadOnlyList<string>> current, string? documentTypeParent,
        List<string> errors, List<string> dropped)
    {
        public SuggestionParseContext Context { get; } = context;

        public IReadOnlyDictionary<string, IReadOnlyList<string>> Current { get; } = current;

        public string? DocumentTypeParent { get; } = documentTypeParent;

        public List<string> Errors { get; } = errors;

        public List<string> Dropped { get; } = dropped;

        public int NewLabelDisagreements { get; set; }
    }

    private static string Shorten(string id) => id.Length <= MaxIdInError ? id : Cut(id, MaxIdInError) + "…";

    /// <summary>First <paramref name="max"/> chars without splitting a surrogate pair.</summary>
    internal static string Cut(string value, int max) =>
        value.Length <= max ? value : value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
}
