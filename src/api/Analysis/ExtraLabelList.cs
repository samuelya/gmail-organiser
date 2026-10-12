using GmailOrganiser.Review;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis;

/// <summary>A change to a suggestion's additional labels: <see cref="Unchanged"/> (default) or set to <see cref="Labels"/>.</summary>
public readonly record struct ExtraLabelsChange(bool IsSet, string[] Labels)
{
    public static readonly ExtraLabelsChange Unchanged = default;

    public static ExtraLabelsChange To(string[] labels) => new(true, labels);
}

/// <summary>
/// The additional labels a suggestion carries next to its topic and document-type labels (#522, DESIGN §6.3), by name.
/// Stored canonical (<see cref="Clean"/>): trimmed, distinct and sorted case-insensitively, so array equality is outcome
/// equality. Shared by review, memory, the analysis parser and policies.
/// </summary>
public static class ExtraLabelList
{
    public const int Max = 5;
    public const string Field = "extraLabels";

    /// <summary>
    /// The canonical list, silently: trims; drops blanks, paths Gmail refuses (<see cref="LabelResolver.IsValid"/>) and
    /// names equal to <paramref name="topic"/>, <paramref name="documentType"/> or a <paramref name="reserved"/> name;
    /// keeps the first spelling of case-insensitive duplicates; sorts <see cref="StringComparer.OrdinalIgnoreCase"/> and
    /// keeps the first <see cref="Max"/>.
    /// </summary>
    public static string[] Clean(
        IEnumerable<string?>? labels, string? topic, string? documentType, IReadOnlyCollection<string> reserved)
    {
        var kept = new List<string>();
        foreach (var raw in labels ?? [])
        {
            var label = raw?.Trim() ?? "";
            if (label.Length > 0 && LabelResolver.IsValid(label) && !Same(label, topic) && !Same(label, documentType)
                && !reserved.Any(r => Same(label, r)) && !kept.Any(k => Same(label, k)))
            {
                kept.Add(label);
            }
        }

        return [.. kept.Order(StringComparer.OrdinalIgnoreCase).Take(Max)];
    }

    /// <summary>The app's own labels, which an additional label must not be: the action and delete labels.</summary>
    public static string[] Reserved(AppSettings settings) => [settings.ActionLabelName, settings.DeleteLabelName];

    /// <summary>
    /// The requested change: null is <see cref="ExtraLabelsChange.Unchanged"/>, <c>[]</c> clears, a list replaces them
    /// (<see cref="Clean"/>ed). Adds a <see cref="Field"/> error naming each blank or invalid entry, each equal to
    /// <paramref name="topic"/>, <paramref name="documentType"/> or an app label, and more than <see cref="Max"/>
    /// distinct ones; nothing changes then.
    /// </summary>
    public static ExtraLabelsChange Normalise(
        string?[]? requested, string? topic, string? documentType, AppSettings settings, Dictionary<string, string[]> errors)
    {
        if (requested is null)
        {
            return ExtraLabelsChange.Unchanged;
        }

        var reserved = Reserved(settings);
        var messages = new List<string>();
        for (var i = 0; i < requested.Length; i++)
        {
            var label = requested[i]?.Trim() ?? "";
            if (Error(label, topic, documentType, reserved) is { } error)
            {
                messages.Add($"Entry {i + 1}{(label.Length == 0 ? "" : $" '{label}'")}: {error}");
            }
        }

        var distinct = requested.Select(l => l?.Trim() ?? "").Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (distinct > Max)
        {
            messages.Add($"At most {Max} additional labels.");
        }

        if (messages.Count > 0)
        {
            errors[Field] = [.. messages];
            return ExtraLabelsChange.Unchanged;
        }

        return ExtraLabelsChange.To(Clean(requested, topic, documentType, reserved));
    }

    /// <summary>
    /// <paramref name="change"/> with each label in Gmail's spelling where Gmail has it (as the topic label edit does);
    /// a label Gmail lacks, or every label when the list cannot be loaded, keeps the typed spelling.
    /// </summary>
    public static async Task<ExtraLabelsChange> ResolveAsync(LabelCatalog labels, ExtraLabelsChange change, CancellationToken ct)
    {
        if (!change.IsSet || change.Labels.Length == 0)
        {
            return change;
        }

        try
        {
            var spelled = new string[change.Labels.Length];
            for (var i = 0; i < spelled.Length; i++)
            {
                spelled[i] = (await labels.FindByNameAsync(change.Labels[i], ct))?.Name.Trim() ?? change.Labels[i];
            }

            return ExtraLabelsChange.To(spelled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return change;
        }
    }

    /// <summary>
    /// Sets <paramref name="change"/> (or keeps the current list) on the suggestion, canonical against its topic and
    /// document type as they are now: an extra the edit made the topic or document type is dropped silently.
    /// </summary>
    public static void Apply(SuggestionRow s, ExtraLabelsChange change, AppSettings settings) =>
        s.ExtraLabels = Clean(change.IsSet ? change.Labels : s.ExtraLabels, s.TopicLabel, s.DocumentTypeLabel, Reserved(settings));

    /// <summary>
    /// The labels Gmail lacks (case-insensitive) from <paramref name="labelNames"/>, in list order; computed, never
    /// stored. Empty when the label list is unknown (null).
    /// </summary>
    public static string[] New(IReadOnlyList<string> labels, IReadOnlyDictionary<string, string>? labelNames)
    {
        if (labelNames is null || labels.Count == 0)
        {
            return [];
        }

        var existing = labelNames.Values.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. labels.Where(l => !existing.Contains(l.Trim()))];
    }

    /// <summary>Whether two canonical lists are the same outcome (case-insensitive, in order).</summary>
    public static bool SameList(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);

    private static string? Error(string label, string? topic, string? documentType, IReadOnlyCollection<string> reserved) =>
        label.Length == 0 ? "Must not be blank."
        : !LabelResolver.IsValid(label) ? "Not a label path Gmail accepts."
        : Same(label, topic) ? "Must differ from the topic label."
        : Same(label, documentType) ? "Must differ from the document-type label."
        : reserved.Any(r => Same(label, r)) ? "The app's action and delete labels are set by the outcome."
        : null;

    private static bool Same(string label, string? other) =>
        other is not null && string.Equals(label, other.Trim(), StringComparison.OrdinalIgnoreCase);
}
