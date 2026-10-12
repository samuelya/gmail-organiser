using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis;

/// <summary>
/// The approved label set of one analysis run (#367): blocked label names fail an answer, a locked taxonomy turns a
/// <c>topicLabel</c> outside the label tree into a <c>proposedNewLabel</c>, a blocked additional label is dropped alone
/// (#529), and past <see cref="MaxNewLabels"/> distinct new labels (locked only; the topic and additional labels outside
/// the tree) a suggestion keeps its labels at a capped confidence.
/// </summary>
/// <param name="admitted">New labels the run already admitted under the cap (from its stored suggestions on resume).</param>
public sealed class ApprovedLabelSet(bool locked, IEnumerable<string> blocked, int maxNewLabels, IEnumerable<string>? admitted = null)
{
    public const double CappedConfidence = 0.5;
    public const string CapNote = " (new-label cap reached)";
    public const string BlockedError = "uses a blocked label name";

    private readonly HashSet<string> _blocked = new(blocked.Select(b => b.Trim()).Where(b => b.Length > 0), StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _admitted = new(admitted ?? [], StringComparer.OrdinalIgnoreCase);

    public bool Locked { get; } = locked;

    public int MaxNewLabels { get; } = maxNewLabels;

    public IReadOnlyCollection<string> Blocked => _blocked;

    public static ApprovedLabelSet From(AppSettings settings, IEnumerable<string>? admitted = null) =>
        new(settings.TaxonomyLocked, settings.AnalysisBlockedLabels, settings.AnalysisMaxNewLabelsPerRun, admitted);

    /// <summary>Any level of <paramref name="label"/> equals a blocked name (case-insensitive).</summary>
    public bool IsBlocked(string? label) =>
        label is not null && _blocked.Count > 0 && label.Split('/').Any(segment => _blocked.Contains(segment.Trim()));

    /// <summary>The answer's topic or document-type label is blocked.</summary>
    public bool IsBlocked(SuggestionOutput output) => IsBlocked(output.TopicLabel) || IsBlocked(output.DocumentTypeLabel);

    /// <summary>
    /// A blocked answer becomes an error for its email (never stored; the job's retry asks again). When locked, a new
    /// <c>topicLabel</c> the model did not propose becomes the proposal, with a note.
    /// </summary>
    public ParsedSuggestions Apply(ParsedSuggestions parsed)
    {
        if (_blocked.Count == 0 && !Locked)
        {
            return parsed;
        }

        var valid = new List<SuggestionOutput>();
        var errors = new List<string>(parsed.Errors);
        var dropped = new List<string>(parsed.Dropped);
        foreach (var output in parsed.Valid)
        {
            if (IsBlocked(output))
            {
                errors.Add($"Email '{output.Id}': the label {BlockedError}.");
            }
            else if (Locked && output.IsNewLabel && output.ProposedNewLabel is null)
            {
                dropped.Add($"Email '{output.Id}': 'topicLabel' is not in the locked label tree; moved to 'proposedNewLabel'.");
                valid.Add(output with { ProposedNewLabel = output.TopicLabel });
            }
            else
            {
                valid.Add(output);
            }
        }

        return parsed with { Valid = valid, Errors = errors, Dropped = dropped };
    }

    /// <summary>
    /// A blocked additional label is dropped alone, the suggestion kept (#529); a locked taxonomy does not drop extras,
    /// which only come from the person's own approved decisions, but one outside <paramref name="tree"/> is a new label.
    /// When locked, a suggestion whose new labels (topic and extras) do not all fit in the run's first
    /// <see cref="MaxNewLabels"/> distinct ones keeps its labels, but its confidence is at most <see cref="CappedConfidence"/>
    /// and its reason ends with <see cref="CapNote"/>.
    /// </summary>
    public SuggestionOutput Admit(SuggestionOutput output, LabelTreeIndex tree)
    {
        var extras = output.ExtraLabels.Where(e => !IsBlocked(e)).ToList();
        if (extras.Count < output.ExtraLabels.Count)
        {
            output = output with { ExtraLabels = extras };
        }

        if (!Locked)
        {
            return output;
        }

        var fresh = NewLabels(output, tree).Where(l => !_admitted.Contains(l)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_admitted.Count + fresh.Count <= MaxNewLabels)
        {
            _admitted.UnionWith(fresh);
            return output;
        }

        return output with
        {
            Confidence = Math.Min(output.Confidence, CappedConfidence),
            Reason = output.Reason.EndsWith(CapNote, StringComparison.Ordinal) ? output.Reason : output.Reason + CapNote,
        };
    }

    /// <summary>The suggestion's labels the cap counts (trimmed): a new topic and the extras outside <paramref name="tree"/>.</summary>
    public static IEnumerable<string> NewLabels(SuggestionOutput output, LabelTreeIndex tree) =>
        NewLabels(output.IsNewLabel, output.TopicLabel, output.ExtraLabels, tree);

    /// <inheritdoc cref="NewLabels(SuggestionOutput, LabelTreeIndex)"/>
    public static IEnumerable<string> NewLabels(bool isNewLabel, string topic, IEnumerable<string> extras, LabelTreeIndex tree) =>
        (isNewLabel ? [topic.Trim()] : Enumerable.Empty<string>()).Concat(extras.Where(e => !tree.Contains(e)).Select(e => e.Trim()));
}
