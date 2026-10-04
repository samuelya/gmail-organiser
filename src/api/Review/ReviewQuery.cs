using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Claude;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>The review sender list and one sender's suggestions grouped by <see cref="SuggestionRow.GroupKey"/>.</summary>
public sealed partial class ReviewQuery(AppDbContext db, ISettingsStore settingsStore, LabelCatalog labelCatalog)
{
    /// <summary>Most members listed for one group.</summary>
    public const int MaxMembers = 500;

    /// <summary>Most members listed in one detail response; every group still lists its newest member.</summary>
    public const int MaxResponseMembers = 2000;

    public const int DefaultGroupPageSize = 20;
    public const int MaxGroupPageSize = 50;

    /// <summary>
    /// Parses <c>pending|approved|rejected|applied</c> (default pending); null when invalid. <c>applied</c> is for the
    /// read paths and the alternative decisions only; the review decisions never act on applied mail.
    /// </summary>
    public static SuggestionStatus? ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "pending" => SuggestionStatus.Pending,
        "approved" => SuggestionStatus.Approved,
        "rejected" => SuggestionStatus.Rejected,
        "applied" => SuggestionStatus.Applied,
        _ => null,
    };

    /// <summary>
    /// Senders with at least one suggestion in <paramref name="status"/>, with their counts per status, the most
    /// suggestions in that status first (pending by default), then by address. With <paramref name="hasAlternative"/>
    /// only suggestions with a compare-run alternative count.
    /// </summary>
    public async Task<PagedDto<ReviewSenderDto>> ListAsync(
        SuggestionStatus status, string? search, int page, int pageSize, CancellationToken ct, bool hasAlternative = false)
    {
        var suggestions = Suggestions(hasAlternative);
        if (search is not null)
        {
            var matching = SenderQuery.Filter(db.Senders.AsNoTracking(), search).Select(x => x.Address);
            suggestions = suggestions.Where(s => matching.Contains(s.SenderAddress));
        }

        var counts = suggestions
            .GroupBy(s => s.SenderAddress)
            .Select(g => new StatusCounts
            {
                Address = g.Key,
                Pending = g.Count(s => s.Status == SuggestionStatus.Pending),
                Approved = g.Count(s => s.Status == SuggestionStatus.Approved),
                Rejected = g.Count(s => s.Status == SuggestionStatus.Rejected),
                Applied = g.Count(s => s.Status == SuggestionStatus.Applied),
            });
        var ordered = status switch
        {
            SuggestionStatus.Approved => counts.Where(c => c.Approved > 0).OrderByDescending(c => c.Approved),
            SuggestionStatus.Rejected => counts.Where(c => c.Rejected > 0).OrderByDescending(c => c.Rejected),
            SuggestionStatus.Applied => counts.Where(c => c.Applied > 0).OrderByDescending(c => c.Applied),
            _ => counts.Where(c => c.Pending > 0).OrderByDescending(c => c.Pending),
        };

        var total = await ordered.LongCountAsync(ct);
        var rows = await ordered.ThenBy(c => c.Address).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var addresses = rows.ConvertAll(r => r.Address);
        var senders = await db.Senders.AsNoTracking()
            .Where(s => addresses.Contains(s.Address))
            .ToDictionaryAsync(s => s.Address, ct);
        return new PagedDto<ReviewSenderDto>([.. rows.Select(r => ToDto(r, senders.GetValueOrDefault(r.Address)))], page, pageSize, total);
    }

    /// <summary>
    /// One page of the sender's suggestions in <paramref name="status"/>, grouped (a message analysed alone is its own
    /// group), largest group first; null when the sender has no suggestion at all. Groups are counted and paged in SQL;
    /// members are loaded per group, newest first, within <see cref="MaxMembers"/> and <see cref="MaxResponseMembers"/>.
    /// With <paramref name="hasAlternative"/> the counts take only suggestions with a compare-run alternative, and only
    /// groups with such a member are listed, whole, so a group card and its approve/reject act on the same members.
    /// </summary>
    public async Task<ReviewSenderDetailDto?> DetailAsync(
        string address, SuggestionStatus status, int page, int pageSize, CancellationToken ct, bool hasAlternative = false)
    {
        var counts = await Suggestions(hasAlternative)
            .Where(s => s.SenderAddress == address)
            .GroupBy(s => s.SenderAddress)
            .Select(g => new StatusCounts
            {
                Address = g.Key,
                Pending = g.Count(s => s.Status == SuggestionStatus.Pending),
                Approved = g.Count(s => s.Status == SuggestionStatus.Approved),
                Rejected = g.Count(s => s.Status == SuggestionStatus.Rejected),
                Applied = g.Count(s => s.Status == SuggestionStatus.Applied),
            })
            .SingleOrDefaultAsync(ct);
        if (counts is null)
        {
            return null;
        }

        var sender = await db.Senders.AsNoTracking().SingleOrDefaultAsync(s => s.Address == address, ct);
        var inStatus = InStatus(address, status, hasAlternative);
        var stats = inStatus
            .GroupBy(s => s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId)
            .Select(g => new GroupStats
            {
                Key = g.Key,
                Size = g.Count(),
                Llm = g.Count(s => s.Source == SuggestionSource.Llm),
                Derived = g.Count(s => s.Source == SuggestionSource.Derived),
                Memory = g.Count(s => s.Source == SuggestionSource.Memory),
                ConfidenceMin = g.Min(s => s.Confidence),
                ConfidenceMax = g.Max(s => s.Confidence),
                NewLabels = g.Count(s => s.IsNewLabel),
            });
        var totalGroups = await stats.LongCountAsync(ct);
        var pageStats = await stats.OrderByDescending(g => g.Size).ThenBy(g => g.Key)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var keys = pageStats.ConvertAll(g => g.Key);
        var outcomes = (await inStatus
                .Where(s => keys.Contains(s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId))
                .GroupBy(s => new { Key = s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId, s.TopicLabel, s.NeedsAction, s.ToBeDeleted, s.DocumentTypeLabel })
                .Select(g => new OutcomeCount(
                    g.Key.Key, g.Key.TopicLabel, g.Key.NeedsAction, g.Key.ToBeDeleted, g.Key.DocumentTypeLabel, g.Count(),
                    g.Count(s => s.Source == SuggestionSource.Llm) > 0))
                .ToListAsync(ct))
            .ToLookup(o => o.Key, StringComparer.Ordinal);

        var groups = new List<ReviewGroupDto>(pageStats.Count);
        var loaded = new List<(GroupStats Stats, List<(SuggestionRow S, MessageRow M)> Members)>(pageStats.Count);
        var budget = MaxResponseMembers;
        foreach (var g in pageStats)
        {
            var take = Math.Clamp(budget, 1, MaxMembers);
            var key = g.Key;
            var members = await (
                    from s in inStatus
                    join m in db.Messages.AsNoTracking() on s.MessageId equals m.Id
                    where (s.GroupKey ?? AnalysisGrouper.IndividualKeyPrefix + s.MessageId) == key
                    orderby m.InternalDate descending, m.Id
                    select new { Suggestion = s, Message = m })
                .Take(take)
                .ToListAsync(ct);
            budget -= members.Count;
            loaded.Add((g, [.. members.Select(r => (r.Suggestion, r.Message))]));
        }

        var claude = await ClaudeLookupAsync(address, loaded, ct);
        var alternatives = await AlternativesAsync(loaded.SelectMany(g => g.Members).Select(x => x.S.Id), ct);
        var alternativeCounts = await AlternativeCountsAsync(inStatus, keys, ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, claude.Settings, ct);
        var personal = await PersonalLabels.LoadAsync(labelCatalog, claude.Settings, ct);
        var labelNames = personal.IsUnavailable ? null : personal.Names;
        foreach (var (g, members) in loaded)
        {
            groups.Add(ToGroup(
                g, outcomes[g.Key], members, allowlist, claude, labelNames, personal.AppLabelIds, alternatives, alternativeCounts.GetValueOrDefault(g.Key)));
        }

        return new ReviewSenderDetailDto(ToDto(counts, sender), groups, page, pageSize, totalGroups);
    }

    /// <summary>
    /// The outcome the card of the sender's group shows over its pending members (what approving the card approves);
    /// null when none is pending.
    /// </summary>
    public async Task<GroupOutcome?> PendingOutcomeAsync(string senderAddress, string groupKey, CancellationToken ct)
    {
        var outcomes = await db.Suggestions.AsNoTracking()
            .Where(s => s.SenderAddress == senderAddress && s.GroupKey == groupKey && s.Status == SuggestionStatus.Pending)
            .GroupBy(s => new { s.TopicLabel, s.NeedsAction, s.ToBeDeleted, s.DocumentTypeLabel })
            .Select(g => new OutcomeCount(
                groupKey, g.Key.TopicLabel, g.Key.NeedsAction, g.Key.ToBeDeleted, g.Key.DocumentTypeLabel, g.Count(),
                g.Count(s => s.Source == SuggestionSource.Llm) > 0))
            .ToListAsync(ct);
        if (outcomes.Count == 0)
        {
            return null;
        }

        var shown = Shown(outcomes);
        return new GroupOutcome(shown.TopicLabel, shown.NeedsAction, shown.ToBeDeleted, DocumentTypeLabel: shown.DocumentTypeLabel);
    }

    /// <summary>
    /// A group's title: the newest member's subject (or the no-subject text), plus the list suffix for a list group and,
    /// with <paramref name="labelNames"/>, the label set the group was split by.
    /// </summary>
    public static string GroupDisplay(string? subject, string? groupKey, IReadOnlyDictionary<string, string>? labelNames = null) =>
        (string.IsNullOrWhiteSpace(subject) ? AnalysisGrouper.NoSubjectDisplay : subject)
        + (groupKey is not null && GroupKey.IsList(groupKey) ? AnalysisGrouper.ListDisplaySuffix : "")
        + (groupKey is not null && labelNames is not null ? AnalysisGrouper.LabelsDisplay(groupKey, labelNames) : "");

    /// <summary>
    /// Worth a Claude review: the reviewer is on and the suggestion is below the threshold (when that rule is on) or
    /// proposes a new label (when that rule is on).
    /// </summary>
    public static bool IsSuggestedForClaude(AppSettings settings, double confidence, bool isNewLabel) =>
        settings.ClaudeReviewerMode != ClaudeReviewerMode.Off
        && ((settings.ClaudeSuggestLowConfidence && confidence < settings.ClaudeSuggestThreshold)
            || (settings.ClaudeSuggestNewLabels && isNewLabel));

    /// <param name="labelNames">Personal label names by id (<see cref="PersonalLabels.Names"/>) for the current labels; null lists none.</param>
    /// <param name="appLabelIds">The action and delete label ids (<see cref="PersonalLabels.AppLabelIds"/>): replaced too
    /// when an accepted alternative replaces an applied outcome.</param>
    public static SuggestionDto ToDto(
        SuggestionRow s,
        MessageRow m,
        Allowlist allowlist,
        ProtectionSettings rules,
        IReadOnlyDictionary<string, string>? labelNames = null,
        IReadOnlyList<string>? appLabelIds = null) =>
        ToDto(s, m, allowlist, rules, labelNames, null, false, appLabelIds: appLabelIds);

    public static SuggestionDto ToDto(
        SuggestionRow s,
        MessageRow m,
        Allowlist allowlist,
        ProtectionSettings rules,
        IReadOnlyDictionary<string, string>? labelNames,
        ExternalReviewDto? claudeReview,
        bool suggestedForClaude,
        SuggestionAlternativeRow? alternative = null,
        IReadOnlyList<string>? appLabelIds = null)
    {
        var current = CurrentLabels(m, labelNames);
        var replaced = Replaced(s.Replaced(labelNames), s.TopicLabel, m, labelNames, appLabelIds);
        return new SuggestionDto(
        s.Id,
        s.MessageId,
        m.Subject,
        m.InternalDate,
        m.Snippet,
        SnakeCaseEnumConverter<SuggestionSource>.ToDb(s.Source),
        s.TopicLabel,
        s.IsNewLabel,
        s.NeedsAction,
        s.ToBeDeleted,
        s.UnsubscribeSuggested,
        s.Confidence,
        s.Reason,
        SnakeCaseEnumConverter<SuggestionStatus>.ToDb(s.Status),
        s.Edited,
        MessageProtection.IsProtected(m, allowlist, rules),
        replaced,
        current,
        LabelChanges.For(s.TopicLabel, replaced, current),
        claudeReview,
        suggestedForClaude,
        s.DocumentTypeLabel,
        s.DocumentTypeIsNew,
        alternative is null ? null : ToDto(alternative, m, current, labelNames));
    }

    /// <summary>
    /// The names of the replaced labels apply would remove now: those the message still carries, never the topic label,
    /// and (when the label list is known) only personal labels or <paramref name="appLabelIds"/> Gmail still has.
    /// Distinct and ordinal-sorted.
    /// </summary>
    private static string[] Replaced(
        IReadOnlyList<(string Id, string Name)> stored,
        string topicLabel,
        MessageRow m,
        IReadOnlyDictionary<string, string>? labelNames,
        IReadOnlyList<string>? appLabelIds = null) =>
        [.. stored
            .Where(l => m.LabelIds.Contains(l.Id, StringComparer.Ordinal)
                && (labelNames is null || labelNames.ContainsKey(l.Id) || appLabelIds?.Contains(l.Id, StringComparer.Ordinal) == true)
                && !string.Equals(l.Name.Trim(), topicLabel.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>The message's personal label names, distinct and ordinal-sorted.</summary>
    private static string[] CurrentLabels(MessageRow m, IReadOnlyDictionary<string, string>? labelNames) =>
        labelNames is null
            ? []
            : [.. m.LabelIds.Select(id => labelNames.GetValueOrDefault(id)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The newest not-cancelled Claude review item per listed suggestion and per group of the page: one query per
    /// target type. A suggestion item is titled from the page (the member's subject); a group item as the
    /// <c>externalReviewChanged</c> event titles it (<see cref="ExternalReviewQuery.ToDtosAsync"/>: newest member over
    /// all statuses, not only the listed tab), so the title doesn't change when an event replaces the item.
    /// </summary>
    private async Task<ClaudeLookup> ClaudeLookupAsync(
        string address, List<(GroupStats Stats, List<(SuggestionRow S, MessageRow M)> Members)> groups, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var subjects = groups.SelectMany(g => g.Members).ToDictionary(x => x.S.Id, x => x.M.Subject);
        var ids = subjects.Keys.ToList();
        var keys = groups.Select(g => g.Members[0].S.GroupKey).OfType<string>().ToList();
        var items = db.ExternalReviews.AsNoTracking()
            .Where(r => r.SenderAddress == address && r.Status != ExternalReviewStatus.Cancelled);
        var suggestionItems = await items
            .Where(r => r.TargetType == ExternalReviewTarget.Suggestion && ids.Contains(r.SuggestionId!.Value))
            .ToListAsync(ct);
        var groupItems = keys.Count == 0
            ? []
            : await items.Where(r => r.TargetType == ExternalReviewTarget.Group && keys.Contains(r.GroupKey!)).ToListAsync(ct);
        var newestGroupItems = Newest(groupItems, r => r.GroupKey!).ToList();
        var groupDtos = newestGroupItems.Count == 0
            ? []
            : await new ExternalReviewQuery(db).ToDtosAsync(newestGroupItems, ct);
        return new ClaudeLookup(
            settings,
            Newest(suggestionItems, r => r.SuggestionId!.Value)
                .ToDictionary(r => r.SuggestionId!.Value, r => ExternalReviewQuery.ToDto(r, GroupDisplay(subjects[r.SuggestionId!.Value], null))),
            groupDtos.ToDictionary(d => d.GroupKey!, StringComparer.Ordinal));

        static IEnumerable<ExternalReviewRow> Newest<TKey>(IEnumerable<ExternalReviewRow> rows, Func<ExternalReviewRow, TKey> key) =>
            rows.GroupBy(key).Select(g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First());
    }

    /// <summary>
    /// The group shows its most common outcome (over all members), with the reason of a listed model-analysed member
    /// that has it when there is one; <c>Mixed</c> when members disagree. Members are newest first.
    /// </summary>
    private static ReviewGroupDto ToGroup(
        GroupStats stats,
        IEnumerable<OutcomeCount> outcomes,
        IReadOnlyList<(SuggestionRow S, MessageRow M)> members,
        Allowlist allowlist,
        ClaudeLookup claude,
        IReadOnlyDictionary<string, string>? labelNames,
        IReadOnlyList<string> appLabelIds,
        IReadOnlyDictionary<Guid, SuggestionAlternativeRow> alternatives,
        int alternativeCount)
    {
        var all = outcomes.ToList();
        var shared = Shown(all);
        bool Matches(SuggestionRow s) =>
            s.TopicLabel == shared.TopicLabel && s.NeedsAction == shared.NeedsAction && s.ToBeDeleted == shared.ToBeDeleted
            && s.DocumentTypeLabel == shared.DocumentTypeLabel;
        var representative = members.FirstOrDefault(x => Matches(x.S) && x.S.Source == SuggestionSource.Llm);
        if (representative.S is null)
        {
            representative = members.FirstOrDefault(x => Matches(x.S));
        }

        if (representative.S is null)
        {
            representative = members[0];
        }

        var newest = members[0];
        var key = newest.S.GroupKey;
        var display = GroupDisplay(newest.M.Subject, key, labelNames);
        var settings = claude.Settings;
        var dtos = members.Select(x => ToDto(
                x.S, x.M, allowlist, settings.Protection, labelNames, claude.Suggestions.GetValueOrDefault(x.S.Id),
                IsSuggestedForClaude(settings, x.S.Confidence, x.S.IsNewLabel), alternatives.GetValueOrDefault(x.S.Id), appLabelIds))
            .ToList();
        var change = Combined(dtos.Select(d => d.LabelChange));
        return new ReviewGroupDto(
            key,
            display,
            stats.Size,
            stats.Llm,
            stats.Derived,
            stats.Memory,
            shared.TopicLabel,
            shared.NeedsAction,
            shared.ToBeDeleted,
            all.Count > 1,
            stats.ConfidenceMin,
            stats.ConfidenceMax,
            representative.S.Reason,
            dtos,
            members.Count < stats.Size,
            [.. dtos.SelectMany(d => d.ReplaceLabels).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            change,
            key is null ? null : claude.Groups.GetValueOrDefault(key),
            IsSuggestedForClaude(settings, stats.ConfidenceMin, stats.NewLabels > 0),
            shared.DocumentTypeLabel,
            shared.DocumentTypeLabel is not null
                && members.Any(x => x.S.DocumentTypeIsNew && x.S.DocumentTypeLabel == shared.DocumentTypeLabel),
            GroupAlternative(dtos, alternativeCount));
    }

    /// <summary>The card's outcome: the most common, then one with a model answer, then by label, flags and type.</summary>
    private static OutcomeCount Shown(IEnumerable<OutcomeCount> outcomes) => outcomes
        .OrderByDescending(o => o.Count)
        .ThenByDescending(o => o.HasLlm)
        .ThenBy(o => o.TopicLabel, StringComparer.Ordinal)
        .ThenBy(o => o.NeedsAction)
        .ThenBy(o => o.ToBeDeleted)
        .ThenBy(o => o.DocumentTypeLabel, StringComparer.Ordinal)
        .First();

    private static ReviewSenderDto ToDto(StatusCounts c, SenderRow? sender) => new(
        c.Address, sender?.DisplayName, c.Pending, c.Approved, c.Rejected, c.Applied, sender?.TotalCount ?? 0);

    /// <summary>A class with settable members (not a record) so EF can filter and sort on the projection.</summary>
    private sealed class StatusCounts
    {
        public string Address { get; init; } = "";
        public int Pending { get; init; }
        public int Approved { get; init; }
        public int Rejected { get; init; }
        public int Applied { get; init; }
    }

    /// <summary>A class with settable members so EF can sort and page on the projection.</summary>
    private sealed class GroupStats
    {
        public string Key { get; init; } = "";
        public int Size { get; init; }
        public int Llm { get; init; }
        public int Derived { get; init; }
        public int Memory { get; init; }
        public double ConfidenceMin { get; init; }
        public double ConfidenceMax { get; init; }
        public int NewLabels { get; init; }
    }

    private sealed record ClaudeLookup(
        AppSettings Settings, Dictionary<Guid, ExternalReviewDto> Suggestions, Dictionary<string, ExternalReviewDto> Groups);

    private sealed record OutcomeCount(
        string Key, string TopicLabel, bool NeedsAction, bool ToBeDeleted, string? DocumentTypeLabel, int Count, bool HasLlm);
}
