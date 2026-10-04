using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;

namespace GmailOrganiser.Claude;

/// <summary>Checks a verdict as <c>submit_review</c> passes it; the message is the client's error.</summary>
internal static class ReviewVerdictValidation
{
    /// <summary>Most label paths a label plan's alternative structure may hold.</summary>
    public const int MaxStructurePaths = 500;

    /// <summary>
    /// <paramref name="documentType"/> is an <c>alternative</c>'s document-type change, checked as the review edit
    /// checks it (<see cref="DocumentTypeEdit.Validate"/>) against <paramref name="parent"/>; unchanged otherwise. A bare
    /// leaf (no '/') is taken as a child of the parent.
    /// </summary>
    public static string? Validate(ReviewVerdictInput v, string? parent, out DocumentTypeChange documentType)
    {
        documentType = DocumentTypeChange.Unchanged;
        if (Validate(v) is { } invalid)
        {
            return invalid;
        }

        if (v.Verdict != ReviewVerdict.Alternative)
        {
            return v.DocumentTypeLabel is null
                ? null
                : "document_type_label is only for 'alternative': to change the document type, answer 'alternative' with topic_label.";
        }

        var requested = v.DocumentTypeLabel?.Trim();
        if (parent is not null && requested is { Length: > 0 } && !requested.Contains('/'))
        {
            requested = $"{parent}/{requested}";
        }

        var errors = new Dictionary<string, string[]>();
        documentType = DocumentTypeEdit.Validate(requested, parent, v.TopicLabel!.Trim(), errors);
        return errors.TryGetValue(DocumentTypeEdit.Field, out var messages) ? $"Document-type label: {messages[0]}" : null;
    }

    /// <summary>
    /// Checks a verdict on a label plan or filter finding item. A finding's <c>alternative</c> needs filter criteria; a
    /// plan's needs <see cref="ReviewVerdictInput.AlternativeStructure"/> (valid label paths), returned as a JSON array in
    /// <paramref name="structure"/>. Neither takes a label.
    /// </summary>
    public static string? ValidateRules(ReviewVerdictInput v, ExternalReviewTarget target, out string? structure)
    {
        structure = null;
        if (Common(v) is { } invalid)
        {
            return invalid;
        }

        if (v.TopicLabel is not null || v.DocumentTypeLabel is not null)
        {
            return target == ExternalReviewTarget.LabelPlan
                ? "A label plan takes comments and alternative_structure, not topic_label or document_type_label."
                : "A filter finding takes filter_criteria, not topic_label or document_type_label.";
        }

        if (target == ExternalReviewTarget.FilterFinding)
        {
            if (v.AlternativeStructure is not null)
            {
                return "alternative_structure is only for label plans (submit_taxonomy_feedback).";
            }

            return v.Verdict == ReviewVerdict.Alternative && string.IsNullOrWhiteSpace(v.FilterCriteria)
                ? "An alternative on a filter finding needs filter_criteria: the filter you propose instead."
                : null;
        }

        if (!string.IsNullOrWhiteSpace(v.FilterCriteria))
        {
            return "filter_criteria is not for label plans.";
        }

        if (v.Verdict != ReviewVerdict.Alternative)
        {
            return v.AlternativeStructure is null ? null : "Only an alternative takes alternative_structure.";
        }

        if (v.AlternativeStructure is not { Count: > 0 and <= MaxStructurePaths } paths)
        {
            return $"An alternative needs alternative_structure: 1 to {MaxStructurePaths} label paths.";
        }

        var trimmed = paths.Select(p => p?.Trim() ?? "").ToList();
        if (trimmed.FindIndex(p => !IsValidLabel(p)) is >= 0 and var bad)
        {
            return $"alternative_structure[{bad}] is not a valid label path: up to five '/'-separated parts, at most {GmailLimits.LabelNameMaxLength} characters, not a Gmail system label.";
        }

        structure = JsonSerializer.Serialize(trimmed);
        return null;
    }

    private static string? Validate(ReviewVerdictInput v)
    {
        if (Common(v) is { } invalid)
        {
            return invalid;
        }

        if (v.Verdict == ReviewVerdict.Alternative && !IsValidLabel(v.TopicLabel))
        {
            return $"An alternative needs a label path: up to five '/'-separated parts, at most {GmailLimits.LabelNameMaxLength} characters, not a Gmail system label.";
        }

        return null;
    }

    private static string? Common(ReviewVerdictInput v)
    {
        if (!Enum.IsDefined(v.Verdict))
        {
            return "Unknown verdict.";
        }

        if (string.IsNullOrWhiteSpace(v.Reasoning))
        {
            return "Reasoning is required.";
        }

        if (!ExternalReviewService.Reviewers.Contains(v.Reviewer))
        {
            return $"Reviewer must be one of {string.Join(", ", ExternalReviewService.Reviewers)}.";
        }

        if (v.Model is { Length: > ExternalReviewService.MaxModelLength })
        {
            return $"Model is at most {ExternalReviewService.MaxModelLength} characters.";
        }

        if (!string.IsNullOrWhiteSpace(v.FilterCriteria))
        {
            if (v.FilterCriteria.Length > ExternalReviewService.MaxFilterCriteriaLength)
            {
                return $"Filter criteria are at most {ExternalReviewService.MaxFilterCriteriaLength} characters.";
            }

            try
            {
                using var _ = JsonDocument.Parse(v.FilterCriteria);
            }
            catch (JsonException)
            {
                return "Filter criteria must be JSON.";
            }
        }

        return null;
    }

    public static bool IsValidLabel(string? label) =>
        label?.Trim() is { Length: > 0 } l && LabelPath.IsValid(l) && !LabelPath.IsReserved(l);
}
