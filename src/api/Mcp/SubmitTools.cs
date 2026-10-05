using System.ComponentModel;
using System.Text.Json;
using GmailOrganiser.Claude;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private const string ToolName = "submit_review";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The write tools' arguments and their JSON types, required ones first; must match <see cref="SubmitReview"/> and
    /// <see cref="RulesTools.SubmitTaxonomyFeedback"/>.
    /// </summary>
    private static readonly Dictionary<string, (string Name, JsonValueKind Kind, bool Required)[]> Arguments = new()
    {
        [ToolName] =
        [
            ("id", JsonValueKind.String, true),
            ("verdict", JsonValueKind.String, true),
            ("reasoning", JsonValueKind.String, true),
            ("topic_label", JsonValueKind.String, false),
            ("needs_action", JsonValueKind.True, false),
            ("to_be_deleted", JsonValueKind.True, false),
            ("document_type_label", JsonValueKind.String, false),
            ("filter_criteria", JsonValueKind.String, false),
            ("model", JsonValueKind.String, false),
        ],
        [RulesTools.SubmitTaxonomyFeedbackName] =
        [
            ("plan_id", JsonValueKind.String, true),
            ("comments", JsonValueKind.String, true),
            ("alternative_structure", JsonValueKind.Array, false),
            ("model", JsonValueKind.String, false),
        ],
    };

    /// <summary>
    /// A call-tool filter that checks the write tools' arguments before the SDK binds them: a missing or mistyped
    /// argument would otherwise throw in the binding and reach the client as a reason-less error (#186).
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> ArgumentFilter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            if (context.Params?.Name is not { } tool || !Arguments.ContainsKey(tool)
                || CheckArguments(context.Params.Arguments, tool) is not { } reason)
            {
                return await next(context, ct);
            }

            var tools = ActivatorUtilities.CreateInstance<SubmitTools>(context.Services!);
            return await tools.RejectArgumentsAsync(context.Params.Arguments, tool, reason, ct);
        };

    /// <summary>Why the arguments don't fit <paramref name="tool"/>'s input schema, or null when they do. Never quotes a value.</summary>
    internal static string? CheckArguments(IDictionary<string, JsonElement>? arguments, string tool = ToolName)
    {
        foreach (var (name, kind, required) in Arguments[tool])
        {
            if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (required)
                {
                    return $"Missing required argument '{name}'.";
                }

                continue;
            }

            var fits = kind == JsonValueKind.True
                ? value.ValueKind is JsonValueKind.True or JsonValueKind.False
                : value.ValueKind == kind && (kind != JsonValueKind.Array || value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String));
            if (!fits)
            {
                var type = kind switch
                {
                    JsonValueKind.True => "a boolean",
                    JsonValueKind.Array => "an array of strings",
                    _ => "a string",
                };
                return $"Argument '{name}' must be {type}.";
            }
        }

        return null;
    }

    private async Task<CallToolResult> RejectArgumentsAsync(
        IDictionary<string, JsonElement>? arguments, string tool, string reason, CancellationToken ct)
    {
        logger.LogWarning("MCP tool {Tool} rejected its arguments: {Reason}", tool, reason);
        var id = arguments?.TryGetValue("id", out var value) == true && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var guid)
            ? guid
            : (Guid?)null;
        SubmitReviewResultDto result = new(false, null, reason);
        if (id is { } known)
        {
            try
            {
                result = await ResultAsync(known, false, reason, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("MCP tool {Tool} could not look up the item status ({Error})", tool, ex.GetType().Name);
            }
        }

        return ToCallToolResult(result);
    }

    [McpServerTool(Name = ToolName, Title = "Submit a review verdict", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Submits your verdict on one review item from list_pending_reviews, once per item. 'agree' keeps the local "
        + "suggestion; 'alternative' proposes topic_label (a full label path, levels separated by '/') with the "
        + "needs_action and to_be_deleted flags, and optionally document_type_label; 'needs_human' leaves it to the user. "
        + "For a filter_finding item, 'agree' keeps the proposed fix and 'alternative' proposes filter_criteria instead (no "
        + "topic_label). A label_plan item takes submit_taxonomy_feedback instead. Nothing is applied until the user "
        + "accepts it in the portal. Returns { ok, status, reason }: with ok=false the verdict was not stored; do not "
        + "retry the same item.")]
    public async Task<CallToolResult> SubmitReview(
        [Description("The review item id from list_pending_reviews.")] string id,
        [Description("One of: agree, alternative, needs_human.")] string verdict,
        [Description("Short reasoning, at most 3 sentences.")] string reasoning,
        [Description("For 'alternative': the label path to use instead.")] string? topic_label = null,
        [Description("For 'alternative': whether the mail needs action by the user.")] bool? needs_action = null,
        [Description("For 'alternative': whether the mail can be marked to be deleted.")] bool? to_be_deleted = null,
        [Description("Only for 'alternative': the document-type label as a full label path, documentTypeParent from "
            + "get_label_tree, '/', and 1 to documentTypeMaxDepth levels of type names, general to specific (only a bare name without '/' "
            + "is put under documentTypeParent; a nested type needs the full path); omit to keep each "
            + "email's own type, empty string for none.")] string? document_type_label = null,
        [Description("Optional Gmail filter criteria: a JSON object as text, or plain text of at most 500 characters.")] string? filter_criteria = null,
        [Description("Optional: the model you are.")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        SubmitReviewResultDto result;
        try
        {
            result = await SubmitAsync(
                id, verdict, reasoning, topic_label, needs_action, to_be_deleted, document_type_label, filter_criteria, model, cancellationToken);
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

        return ToCallToolResult(result);
    }

    internal static CallToolResult ToCallToolResult(SubmitReviewResultDto result)
    {
        var json = JsonSerializer.SerializeToElement(result, Json);
        return new CallToolResult
        {
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }],
        };
    }

    private async Task<SubmitReviewResultDto> SubmitAsync(
        string id, string verdict, string reasoning, string? topicLabel, bool? needsAction, bool? toBeDeleted,
        string? documentTypeLabel, string? filterCriteria, string? model, CancellationToken ct)
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

        if (await db.ExternalReviews.AnyAsync(r => r.Id == guid && r.TargetType == ExternalReviewTarget.LabelPlan, ct))
        {
            return await ResultAsync(guid, false, "A label plan item takes submit_taxonomy_feedback with its plan id, not submit_review.", ct);
        }

        var input = new ReviewVerdictInput(parsed, topicLabel, needsAction, toBeDeleted, criteria, reasoning ?? "", Reviewer, model, documentTypeLabel);
        var (outcome, invalid) = await reviews.SubmitVerdictAsync(guid, input, ct);
        return await ResultAsync(guid, outcome == ReviewVerdictResult.Ok, Reason(outcome, invalid), ct);
    }

    /// <summary>The client's reason for <paramref name="outcome"/>; null when <c>Ok</c>.</summary>
    internal static string? Reason(ReviewVerdictResult outcome, string? invalid) => outcome switch
    {
        ReviewVerdictResult.Ok => null,
        ReviewVerdictResult.NotFound => "No review item with this id.",
        ReviewVerdictResult.AlreadyReviewed => "This item is already reviewed.",
        ReviewVerdictResult.Closed => "This item was cancelled or marked unavailable; nobody is waiting for a verdict.",
        ReviewVerdictResult.AlreadyDecided => "The user already decided this item's suggestions, plan or finding; skip it.",
        _ => invalid ?? "Invalid verdict.",
    };

    private Task<SubmitReviewResultDto> ResultAsync(Guid id, bool ok, string? reason, CancellationToken ct) =>
        ResultAsync(db, id, ok, reason, ct);

    /// <summary>The result with the item's status after the call.</summary>
    internal static async Task<SubmitReviewResultDto> ResultAsync(AppDbContext db, Guid id, bool ok, string? reason, CancellationToken ct)
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
    /// Blank and JSON <c>null</c> are none; a JSON object or string is kept as given; any other text (including other
    /// JSON literals) of at most <see cref="MaxPlainFilterCriteriaLength"/> characters is stored as a JSON string (the
    /// column is jsonb). M6 interprets it.
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
            using var doc = JsonDocument.Parse(value);
            switch (doc.RootElement.ValueKind)
            {
                case JsonValueKind.Null:
                    return true;
                case JsonValueKind.Object or JsonValueKind.String:
                    criteria = value;
                    return true;
            }
        }
        catch (JsonException)
        {
        }

        var text = value.Trim();
        criteria = text.Length <= MaxPlainFilterCriteriaLength ? JsonSerializer.Serialize(text) : null;
        return criteria is not null;
    }
}
