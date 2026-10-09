using GmailOrganiser.Data;
using GmailOrganiser.Llm;
using GmailOrganiser.Rules.Prompts;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Rules.Review;

public enum SummaryOutcome
{
    Ok,
    NotFound,

    /// <summary>The review has no findings to summarise.</summary>
    NoFindings,

    /// <summary>No chat model is chosen in Settings, or no usable Claude API key is set (see <see cref="SummaryResult.Detail"/>).</summary>
    NotConfigured,
}

/// <param name="Review">Set for <see cref="SummaryOutcome.Ok"/>, also when the model failed (see <see cref="FilterReviewDto.SummaryError"/>).</param>
/// <param name="Detail">For <see cref="SummaryOutcome.NotConfigured"/>: what is missing (no chat model, or no Claude API key).</param>
public sealed record SummaryResult(SummaryOutcome Outcome, FilterReviewDto? Review = null, string? Detail = null);

/// <summary>
/// Writes the local chat model's plain-text reading of a filter review's findings (DESIGN §6.5) to the review. A model
/// failure is recorded in <see cref="FilterReviewRow.SummaryError"/> and keeps the previous summary.
/// </summary>
public sealed class FilterReviewSummariser(
    AppDbContext db,
    FilterSnapshot snapshot,
    FilterReviewService reviews,
    ILlmClientFactory llm,
    TimeProvider time,
    ILogger<FilterReviewSummariser> logger)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public async Task<SummaryResult> SummariseAsync(Guid id, CancellationToken ct)
    {
        var review = await db.FilterReviews.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (review is null)
        {
            return new SummaryResult(SummaryOutcome.NotFound);
        }

        var findings = await db.FilterFindings.AsNoTracking().Where(f => f.ReviewId == id).OrderBy(f => f.Id).ToListAsync(ct);
        if (findings.Count == 0)
        {
            return new SummaryResult(SummaryOutcome.NoFindings);
        }

        // The shared start check: a Claude provider without a usable key says so, not "choose a model".
        ChatConfiguration chatConfig;
        try
        {
            chatConfig = await llm.EnsureChatConfiguredAsync(ct);
        }
        catch (LlmNotConfiguredException ex)
        {
            return new SummaryResult(SummaryOutcome.NotConfigured, Detail: ex.Message);
        }

        var model = chatConfig.Model;

        // Without the label catalog every label would read as deleted and the model would call the fixes broken.
        if (await snapshot.TryLabelNamesAsync(ct) is not { } names)
        {
            review.SummaryError = "Gmail labels could not be read (Gmail not connected or rate-limited); try again later.";
            await db.SaveChangesAsync(CancellationToken.None);
            return new SummaryResult(SummaryOutcome.Ok, await reviews.GetAsync(id, CancellationToken.None));
        }

        var ids = findings.SelectMany(f => f.FilterIds.Concat(f.ReadFix().DeleteFilterIds)).Distinct().ToList();
        var filters = (await db.Filters.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct))
            .OrderBy(r => ids.IndexOf(r.Id)).Select(r => FilterSnapshot.ToDto(r, names)).ToList();
        var messages = RulesSummaryPromptBuilder.Build(filters, [.. findings.Select(f => ToSummaryFinding(f, names))]);

        try
        {
            using var chat = llm.CreateChatClient(chatConfig);
            using var timeout = new CancellationTokenSource(Timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var response = await chat.GetResponseAsync(messages, RulesSummaryPromptBuilder.CreateOptions(), linked.Token);
            if (RulesSummaryPromptBuilder.Clean(response.Text) is not { } summary)
            {
                review.SummaryError = "The model returned an empty answer.";
            }
            else
            {
                review.Summary = summary;
                review.SummaryModel = model;
                review.SummaryPromptVersion = RulesSummaryPromptBuilder.Version;
                review.SummarisedAt = time.GetUtcNow();
                review.SummaryError = null;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Timeout, connection refused, model errors: recorded, never a 500; the previous summary stays.
            logger.LogWarning(ex, "Filter review {ReviewId} summary failed", id);
            review.SummaryError = ex is OperationCanceledException
                ? $"The model did not answer within {Timeout.TotalSeconds:0} seconds."
                : $"The model call failed: {ex.Message}";
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return new SummaryResult(SummaryOutcome.Ok, await reviews.GetAsync(id, CancellationToken.None));
    }

    private static SummaryFinding ToSummaryFinding(FilterFindingRow f, IReadOnlyDictionary<string, string> names)
    {
        var fix = f.ReadFix();
        FilterDto? create = null;
        if (fix.Create is { } c)
        {
            create = FilterSnapshot.ToDto(
                new FilterRow
                {
                    Criteria = FilterRow.WriteCriteria(c.Criteria),
                    Action = FilterRow.WriteAction(c.Action),
                    CriteriaSummary = FilterSnapshot.Summarise(c.Criteria),
                    CreatedByApp = true,
                },
                names);
        }

        return new SummaryFinding(f.Kind, f.Status, f.FilterIds, f.Description, fix.Kind, fix.DeleteFilterIds, create);
    }
}
