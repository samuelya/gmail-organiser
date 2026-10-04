using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;

namespace GmailOrganiser.Claude;

/// <summary>Checks a verdict as <c>submit_review</c> passes it; the message is the client's error.</summary>
internal static class ReviewVerdictValidation
{
    /// <summary>
    /// <paramref name="documentType"/> is an <c>alternative</c>'s document-type change, checked as the review edit
    /// checks it (<see cref="DocumentTypeEdit.Validate"/>) against <paramref name="parent"/>; unchanged otherwise.
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
            return null;
        }

        var errors = new Dictionary<string, string[]>();
        documentType = DocumentTypeEdit.Validate(v.DocumentTypeLabel, parent, v.TopicLabel!.Trim(), errors);
        return errors.TryGetValue(DocumentTypeEdit.Field, out var messages) ? $"Document-type label: {messages[0]}" : null;
    }

    private static string? Validate(ReviewVerdictInput v)
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

        if (v.Verdict == ReviewVerdict.Alternative && !IsValidLabel(v.TopicLabel))
        {
            return $"An alternative needs a label path: up to five '/'-separated parts, at most {GmailLimits.LabelNameMaxLength} characters, not a Gmail system label.";
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
