using System.ComponentModel;
using System.Text.Json;
using GmailOrganiser.Claude;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GmailOrganiser.Mcp;

/// <param name="Status">The item's status after the call (snake_case); null for an unknown id.</param>
/// <param name="Reason">Why the verdict was not stored; null when <c>Ok</c>.</param>
public sealed record SubmitReviewResultDto(bool Ok, string? Status, string? Reason);

/// <summary>
/// <c>submit_review</c> (DESIGN §6.7): stores Claude's verdict on a review item through
/// <see cref="ExternalReviewService.SubmitVerdictAsync"/>. It never touches Gmail or the local suggestions; the user
/// accepts or dismisses the verdict in the portal. Every outcome is a <see cref="SubmitReviewResultDto"/>, never an
/// exception on the transport. Arguments are never logged (email-derived reasoning).
/// </summary>
[McpServerToolType]
public sealed class SubmitTools(ExternalReviewService reviews, AppDbContext db, ILogger<SubmitTools> logger)
{
    public const string Reviewer = "mcp";
    public const int MaxPlainFilterCriteriaLength = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "submit_review", Title = "Submit a review verdict", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Submits your verdict on one review item from list_pending_reviews, once per item. 'agree' keeps the local "
        + "suggestion; 'alternative' proposes topic_label (a full label path, levels separated by '/') with the "
        + "needs_action and to_be_deleted flags; 'needs_human' leaves it to the user. Nothing is applied until the user "
        + "accepts it in the portal. Returns { ok, status, reason }: with ok=false the verdict was not stored; do not "
        + "retry the same item.")]
    public async Task<CallToolResult> SubmitReview(
        [Description("The review item id from list_pending_reviews.")] string id,
        [Description("One of: agree, alternative, needs_human.")] string verdict,
        [Description("Short reasoning, at most 3 sentences.")] string reasoning,
        [Description("For 'alternative': the label path to use instead.")] string? topic_label = null,
        [Description("For 'alternative': whether the mail needs action by the user.")] bool? needs_action = null,
        [Description("For 'alternative': whether the mail can be marked to be deleted.")] bool? to_be_deleted = null,
        [Description("Optional Gmail filter criteria: a JSON object as text, or plain text of at most 500 characters.")] string? filter_criteria = null,
        [Description("Optional: the model you are.")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        SubmitReviewResultDto result;
        try
        {
            result = await SubmitAsync(id, verdict, reasoning, topic_label, needs_action, to_be_deleted, filter_criteria, model, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("MCP tool submit_review failed ({Error})", ex.GetType().Name);
            result = new(false, null, "submit_review failed; see the API log.");
        }

        var json = JsonSerializer.SerializeToElement(result, Json);
        return new CallToolResult
        {
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }],
        };
    }

    private async Task<SubmitReviewResultDto> SubmitAsync(
        string id, string verdict, string reasoning, string? topicLabel, bool? needsAction, bool? toBeDeleted,
        string? filterCriteria, string? model, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return new(false, null, "No review item with this id.");
        }

        if (ParseVerdict(verdict) is not { } parsed)
        {
            return await ResultAsync(guid, false, "Verdict must be one of agree, alternative, needs_human.", ct);
        }

        if (!TryFilterCriteria(filterCriteria, out var criteria))
        {
            return await ResultAsync(guid, false, $"Filter criteria must be JSON or plain text of at most {MaxPlainFilterCriteriaLength} characters.", ct);
        }

        var input = new ReviewVerdictInput(parsed, topicLabel, needsAction, toBeDeleted, criteria, reasoning ?? "", Reviewer, model);
        var (outcome, invalid) = await reviews.SubmitVerdictAsync(guid, input, ct);
        var reason = outcome switch
        {
            ReviewVerdictResult.Ok => null,
            ReviewVerdictResult.NotFound => "No review item with this id.",
            ReviewVerdictResult.AlreadyReviewed => "This item is already reviewed.",
            ReviewVerdictResult.Closed => "This item was cancelled or marked unavailable; nobody is waiting for a verdict.",
            ReviewVerdictResult.AlreadyDecided => "The user already decided this item's suggestions; skip it.",
            _ => invalid ?? "Invalid verdict.",
        };
        return await ResultAsync(guid, outcome == ReviewVerdictResult.Ok, reason, ct);
    }

    private async Task<SubmitReviewResultDto> ResultAsync(Guid id, bool ok, string? reason, CancellationToken ct)
    {
        var status = await db.ExternalReviews.AsNoTracking().Where(r => r.Id == id).Select(r => (ExternalReviewStatus?)r.Status)
            .SingleOrDefaultAsync(ct);
        return new(ok, status is { } s ? SnakeCaseEnumConverter<ExternalReviewStatus>.ToDb(s) : null, reason);
    }

    /// <summary>Tolerates case, surrounding spaces and <c>needs-human</c> as DESIGN §6.7 spells it.</summary>
    internal static ReviewVerdict? ParseVerdict(string? verdict) =>
        verdict?.Trim().Replace('-', '_').ToLowerInvariant() switch
        {
            "agree" => ReviewVerdict.Agree,
            "alternative" => ReviewVerdict.Alternative,
            "needs_human" => ReviewVerdict.NeedsHuman,
            _ => null,
        };

    /// <summary>
    /// Blank is null; JSON text is kept as given; other text of at most <see cref="MaxPlainFilterCriteriaLength"/>
    /// characters is stored as a JSON string (the column is jsonb). M6 interprets it.
    /// </summary>
    internal static bool TryFilterCriteria(string? value, out string? criteria)
    {
        criteria = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        try
        {
            using var _ = JsonDocument.Parse(value);
            criteria = value;
            return true;
        }
        catch (JsonException)
        {
            var text = value.Trim();
            criteria = text.Length <= MaxPlainFilterCriteriaLength ? JsonSerializer.Serialize(text) : null;
            return criteria is not null;
        }
    }
}
