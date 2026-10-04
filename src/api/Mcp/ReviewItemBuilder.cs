using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Claude;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Mcp;

/// <summary>The local LLM's suggestion for a review item; <c>Source</c> is snake_case.</summary>
public sealed record LocalSuggestionDto(
    string TopicLabel, bool IsNewLabel, bool NeedsAction, bool ToBeDeleted, double Confidence, string Reason, string Source);

/// <summary>A review item as <c>list_pending_reviews</c> lists it; <c>Local</c> is null when nothing is left to review.</summary>
public sealed record PendingReviewDto(
    Guid Id, string TargetType, string Sender, string? GroupDisplay, int MemberCount, LocalSuggestionDto? Local);

/// <param name="Status">The status listed: <c>running</c> when any item is running, else <c>queued</c>.</param>
public sealed record PendingReviewsDto(string Status, IReadOnlyList<PendingReviewDto> Items);

/// <param name="Body">The cleaned body when asked for with <c>include_bodies</c>; read from Gmail, never stored.</param>
public sealed record SampleMessageDto(
    string Id, string? Subject, DateTimeOffset Date, string? Snippet, IReadOnlyList<string> Labels, bool HasAttachment, string? ListId, string? Body);

public sealed record ReviewItemDetailDto(
    PendingReviewDto Item,
    string Status,
    SenderDto? SenderStats,
    IReadOnlyList<SampleMessageDto> Samples,
    IReadOnlyList<string> LabelTree,
    IReadOnlyList<MemoryHint> SimilarDecisions);

/// <summary>Assembles the read-only review item views the MCP tools return. Never writes, never logs email content.</summary>
public sealed class ReviewItemBuilder(
    AppDbContext db,
    ExternalReviewQuery reviews,
    ReviewQuery reviewQuery,
    LabelTreeBuilder labels,
    IDecisionMemory memory,
    IGmailClient gmail,
    ISettingsStore settings,
    ILogger<ReviewItemBuilder> logger)
{
    public const int DefaultListLimit = 20;
    public const int MaxListLimit = 100;
    public const int MaxSamples = 5;
    public const int MaxSimilarDecisions = 10;

    /// <summary>
    /// Running items while a run is in progress, else queued ones; oldest first, at most <paramref name="limit"/> (clamped).
    /// Items whose suggestions the user decided after sending them are skipped: <c>submit_review</c> refuses them. A run
    /// whose items were all decided lists none, so Claude never reviews items outside the batch it was started for.
    /// </summary>
    public async Task<PendingReviewsDto> ListPendingAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        var open = db.ExternalReviews.AsNoTracking().Where(r => db.Suggestions.Any(s => s.Status == SuggestionStatus.Pending
            && (r.TargetType == ExternalReviewTarget.Suggestion
                ? s.Id == r.SuggestionId
                : s.SenderAddress == r.SenderAddress && s.GroupKey == r.GroupKey)));
        var status = await db.ExternalReviews.AnyAsync(r => r.Status == ExternalReviewStatus.Running, ct)
            ? ExternalReviewStatus.Running
            : ExternalReviewStatus.Queued;
        var rows = await open
            .Where(r => r.Status == status)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);
        var dtos = await reviews.ToDtosAsync(rows, ct);
        var items = new List<PendingReviewDto>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var members = await MembersAsync(rows[i], ct);
            items.Add(ToItem(rows[i], dtos[i].GroupDisplay, members, await LocalAsync(rows[i], members, ct)));
        }

        return new PendingReviewsDto(SnakeCaseEnumConverter<ExternalReviewStatus>.ToDb(status), items);
    }

    /// <summary>The item with its context, or null for an unknown id.</summary>
    public async Task<ReviewItemDetailDto?> GetAsync(Guid id, bool includeBodies, CancellationToken ct)
    {
        if (await db.ExternalReviews.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is not { } row)
        {
            return null;
        }

        var display = (await reviews.ToDtosAsync([row], ct))[0].GroupDisplay;
        var members = await MembersAsync(row, ct);
        var item = ToItem(row, display, members, await LocalAsync(row, members, ct));

        var sender = await db.Senders.AsNoTracking().SingleOrDefaultAsync(s => s.Address == row.SenderAddress, ct);
        var sampleIds = members
            .OrderByDescending(m => m.Source == SuggestionSource.Llm)
            .ThenByDescending(m => m.Date)
            .ThenBy(m => m.MessageId, StringComparer.Ordinal)
            .Take(MaxSamples)
            .Select(m => m.MessageId)
            .ToList();
        var messages = (await db.Messages.AsNoTracking().Where(m => sampleIds.Contains(m.Id)).ToListAsync(ct))
            .OrderBy(m => sampleIds.IndexOf(m.Id))
            .ToList();

        var allLabels = await labels.LabelsAsync(ct);
        var names = allLabels.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        var current = await settings.GetAsync(ct);
        var maxChars = current.AnalysisBodyMaxChars;
        var samples = new List<SampleMessageDto>(messages.Count);
        foreach (var m in messages)
        {
            samples.Add(new SampleMessageDto(
                m.Id,
                m.Subject,
                m.InternalDate,
                m.Snippet,
                [.. m.LabelIds.Select(l => names.GetValueOrDefault(l, l))],
                m.HasAttachment,
                m.ListId,
                includeBodies ? await BodyAsync(m.Id, maxChars, ct) : null));
        }

        var vectors = messages.Count == 0 ? null : await memory.EmbedMessagesAsync(messages, ct);
        var similar = await memory.FindSimilarAsync(messages, vectors, MaxSimilarDecisions, current.DocumentTypeParent, ct);

        return new ReviewItemDetailDto(
            item,
            SnakeCaseEnumConverter<ExternalReviewStatus>.ToDb(row.Status),
            sender is null ? null : SenderQuery.ToDto(sender, current.Protection.AllowlistedDomains),
            samples,
            await labels.NamesAsync(AnalysisPromptBuilder.MaxLabelTreeEntries, ct),
            similar);
    }

    private sealed record Member(
        string MessageId, DateTimeOffset Date, SuggestionSource Source, string TopicLabel, bool IsNewLabel,
        bool NeedsAction, bool ToBeDeleted, double Confidence, string Reason, SuggestionStatus Status);

    /// <summary>The target's suggestions: the pending ones when any are pending, else all of them; newest first.</summary>
    private async Task<List<Member>> MembersAsync(ExternalReviewRow row, CancellationToken ct)
    {
        var suggestions = row.TargetType == ExternalReviewTarget.Group
            ? db.Suggestions.Where(s => s.SenderAddress == row.SenderAddress && s.GroupKey == row.GroupKey)
            : db.Suggestions.Where(s => s.Id == row.SuggestionId);
        var all = await (
                from s in suggestions.AsNoTracking()
                join m in db.Messages.AsNoTracking() on s.MessageId equals m.Id
                orderby m.InternalDate descending, m.Id
                select new Member(s.MessageId, m.InternalDate, s.Source, s.TopicLabel, s.IsNewLabel, s.NeedsAction,
                    s.ToBeDeleted, s.Confidence, s.Reason, s.Status))
            .ToListAsync(ct);
        var pending = all.Where(m => m.Status == SuggestionStatus.Pending).ToList();
        return pending.Count > 0 ? pending : all;
    }

    /// <summary>
    /// The outcome the portal shows for the target (a group's card outcome, as <see cref="ReviewQuery"/> picks it),
    /// with the confidence and reason of a member that has it, preferring a model-analysed one.
    /// </summary>
    private async Task<LocalSuggestionDto?> LocalAsync(ExternalReviewRow row, IReadOnlyList<Member> members, CancellationToken ct)
    {
        if (members.Count == 0)
        {
            return null;
        }

        var shown = row.TargetType == ExternalReviewTarget.Group
            ? await reviewQuery.PendingOutcomeAsync(row.SenderAddress, row.GroupKey!, ct)
            : null;
        bool Matches(Member m) =>
            shown is null || (m.TopicLabel == shown.TopicLabel && m.NeedsAction == shown.NeedsAction && m.ToBeDeleted == shown.ToBeDeleted);
        var pick = members.FirstOrDefault(m => Matches(m) && m.Source == SuggestionSource.Llm)
            ?? members.FirstOrDefault(Matches)
            ?? members[0];
        return new LocalSuggestionDto(
            pick.TopicLabel, pick.IsNewLabel, pick.NeedsAction, pick.ToBeDeleted, pick.Confidence, pick.Reason,
            SnakeCaseEnumConverter<SuggestionSource>.ToDb(pick.Source));
    }

    private static PendingReviewDto ToItem(ExternalReviewRow row, string? display, IReadOnlyList<Member> members, LocalSuggestionDto? local) => new(
        row.Id,
        SnakeCaseEnumConverter<ExternalReviewTarget>.ToDb(row.TargetType),
        row.SenderAddress,
        display,
        members.Count,
        local);

    /// <summary>The cleaned body, or null when Gmail cannot return it. Never logged or stored.</summary>
    private async Task<string?> BodyAsync(string messageId, int maxChars, CancellationToken ct)
    {
        try
        {
            return await gmail.GetMessageBodyAsync(messageId, ct) is { } body ? BodyCleaner.Clean(body.Text, body.Html, maxChars) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("A sample body could not be read from Gmail ({Error})", ex.GetType().Name);
            return null;
        }
    }
}
