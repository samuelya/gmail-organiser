using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// Validates the model's JSON answer. Never throws on model output: anything unusable becomes an error string
/// (ids and field names only, never email content) and the caller records it as a failure.
/// </summary>
public static partial class SuggestionOutputParser
{
    public const int MaxLabelPathLength = 225;
    public const int MaxReasonLength = 300;
    public const int MaxFilterValueLength = 200;
    public const double ConfidenceTolerance = 0.01;

    private const int MaxIdInError = 64;

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

        var start = string.IsNullOrEmpty(raw) ? -1 : raw.IndexOfAny(['[', '{']);
        if (start < 0)
        {
            errors.Add("Output contains no JSON array or object.");
            return Finish(valid, errors, filter, expectedIds, []);
        }

        var document = ReadFirstValue(raw![start..]);
        if (document is null)
        {
            errors.Add("Output is not valid JSON.");
            return Finish(valid, errors, filter, expectedIds, []);
        }

        var answered = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        using (document)
        {
            var root = document.RootElement;
            var (items, wrapped) = Items(root);
            if (wrapped)
            {
                filter = ReadFilter(root, errors);
            }

            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Array item is not an object.");
                    continue;
                }

                filter ??= ReadFilter(item, errors);
                if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || idElement.GetString() is not { Length: > 0 } id)
                {
                    errors.Add("Item has no string 'id'.");
                    continue;
                }

                if (!expectedIds.Contains(id))
                {
                    errors.Add($"Unknown id '{Shorten(id)}'.");
                    continue;
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
            }
        }

        return Finish(valid, errors, filter, expectedIds, answered);
    }

    /// <summary>
    /// Reads one JSON value from the start of <paramref name="json"/> and ignores whatever follows it (code fences,
    /// trailing prose); null when that value is not valid JSON.
    /// </summary>
    private static JsonDocument? ReadFirstValue(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), ReaderOptions);
        try
        {
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
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
                reason!.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason);
    }

    /// <summary>Up to five <c>/</c>-separated segments, none blank or starting with whitespace, at most 225 chars.</summary>
    public static bool IsValidLabelPath(string path) =>
        path.Length <= MaxLabelPathLength && !path.Any(char.IsControl) && LabelPathRegex().IsMatch(path);

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

    private static string Shorten(string id) => id.Length <= MaxIdInError ? id : id[..MaxIdInError] + "…";

    [GeneratedRegex(@"^[^/\s][^/]{0,99}(/[^/\s][^/]{0,99}){0,4}$")]
    private static partial Regex LabelPathRegex();
}
