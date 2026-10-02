using System.Text;
using System.Text.Json;
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

    public static ParsedSuggestions Parse(string? raw, IReadOnlySet<string> expectedIds)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);
        var valid = new List<SuggestionOutput>();
        var errors = new List<string>();
        FilterCriteriaOutput? filter = null;

        var document = ReadFirstValue(raw ?? string.Empty, out var candidates);
        if (document is null)
        {
            errors.Add(candidates == 0 ? "Output contains no JSON array or object." : "Output is not valid JSON.");
            return Finish(valid, errors, filter, expectedIds, []);
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
                return Finish(valid, errors, null, expectedIds, []);
            }

            foreach (var item in items)
            {
                try
                {
                    filter = ReadItem(item, expectedIds, errors, filter, answered, accepted, valid);
                }
                catch (InvalidOperationException)
                {
                    errors.Add(InvalidText);
                }
            }
        }

        return Finish(valid, errors, filter, expectedIds, answered);
    }

    private static FilterCriteriaOutput? ReadItem(JsonElement item, IReadOnlySet<string> expectedIds, List<string> errors,
        FilterCriteriaOutput? filter, HashSet<string> answered, HashSet<string> accepted, List<SuggestionOutput> valid)
    {
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
        else if (ReadSuggestion(id, item, errors) is { } suggestion)
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
    private static JsonDocument? ReadFirstValue(string raw, out int candidates)
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

    private static SuggestionOutput? ReadSuggestion(string id, JsonElement item, List<string> errors)
    {
        var count = errors.Count;
        var label = ReadString(item, "topicLabel")?.Trim();
        if (label is null || !IsValidLabelPath(label))
        {
            errors.Add($"Email '{id}': 'topicLabel' is missing or not a valid label path.");
        }
        else if (LabelPath.IsReserved(label))
        {
            errors.Add($"Email '{id}': 'topicLabel' is a Gmail system label.");
        }

        var isNewLabel = ReadBool(id, item, "isNewLabel", errors);
        var needsAction = ReadBool(id, item, "needsAction", errors);
        var toBeDeleted = ReadBool(id, item, "toBeDeleted", errors);
        var unsubscribe = ReadBool(id, item, "unsubscribeSuggested", errors);
        var confidence = ReadConfidence(id, item, errors);
        var reason = ReadString(item, "reason")?.Trim();
        if (reason is null)
        {
            errors.Add($"Email '{id}': 'reason' must be a string.");
        }

        return errors.Count > count
            ? null
            : new SuggestionOutput(id, label!, isNewLabel, needsAction, toBeDeleted, unsubscribe, confidence,
                Cut(reason!, MaxReasonLength));
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
        IReadOnlySet<string> expectedIds, HashSet<string> answered)
    {
        errors.AddRange(expectedIds.Where(id => !answered.Contains(id)).Order(StringComparer.Ordinal).Select(id => $"Email '{id}': no answer."));
        return new ParsedSuggestions(valid, errors, filter);
    }

    private static string Shorten(string id) => id.Length <= MaxIdInError ? id : Cut(id, MaxIdInError) + "…";

    /// <summary>First <paramref name="max"/> chars without splitting a surrogate pair.</summary>
    private static string Cut(string value, int max) =>
        value.Length <= max ? value : value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
}
