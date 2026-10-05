using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis;

/// <summary>
/// The approved label set of one analysis run (#367): blocked label names fail an answer, a locked taxonomy turns a
/// <c>topicLabel</c> outside the label tree into a <c>proposedNewLabel</c>, and past <see cref="MaxNewLabels"/> distinct
/// new labels (locked only) a suggestion keeps its label at a capped confidence.
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
    /// When locked, a new label beyond the run's first <see cref="MaxNewLabels"/> distinct ones keeps its label, but its
    /// confidence is at most <see cref="CappedConfidence"/> and its reason ends with <see cref="CapNote"/>.
    /// </summary>
    public SuggestionOutput Admit(SuggestionOutput output)
    {
        if (!Locked || !output.IsNewLabel)
        {
            return output;
        }

        var label = output.TopicLabel.Trim();
        if (_admitted.Contains(label) || (_admitted.Count < MaxNewLabels && _admitted.Add(label)))
        {
            return output;
        }

        return output with
        {
            Confidence = Math.Min(output.Confidence, CappedConfidence),
            Reason = output.Reason.EndsWith(CapNote, StringComparison.Ordinal) ? output.Reason : output.Reason + CapNote,
        };
    }
}
