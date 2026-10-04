using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>The outcome a group card shows: label, needs-action and to-be-deleted.</summary>
/// <param name="ReplaceLabels">
/// Each member keeps only its own replaced labels named here (never gains one; empty clears them); null leaves them.
/// </param>
public sealed record GroupOutcome(string TopicLabel, bool NeedsAction, bool ToBeDeleted, IReadOnlyList<string>? ReplaceLabels = null);

public enum ReviewResult
{
    Ok,
    NotFound,

    /// <summary>Already applied (or, for analyse individually, every suggestion is approved or applied).</summary>
    Conflict,

    /// <summary>A replaced label the message (or no pending group member) carries.</summary>
    InvalidReplaceLabels,

    /// <summary>The replaced labels need the Gmail label list, and it cannot be loaded now.</summary>
    LabelsUnavailable,
}

/// <summary>
/// Review decisions. Every change locks its suggestion rows (<c>FOR UPDATE</c>) and re-checks the status under the
/// lock, then changes the status through <see cref="SuggestionRow.SetStatus"/> and records one decision in the same
/// transaction, so a concurrent apply is never overwritten. Nothing here touches Gmail. Endpoints validate first.
/// </summary>
public sealed class ReviewService(
    AppDbContext db,
    DecisionRecorder decisions,
    AnalysisRunService runs,
    LabelCatalog labels,
    ISettingsStore settingsStore,
    TimeProvider time)
{
    /// <summary>Rows per transaction for group and bulk decisions.</summary>
    public const int ChunkSize = 1000;

    /// <summary>Most skipped ids a group or bulk response lists.</summary>
    public const int MaxSkippedIds = 1000;

    /// <summary>
    /// <c>Pending → Approved|Rejected</c> and <c>Approved ↔ Rejected</c>; the same status again is a no-op without a
    /// decision row; <see cref="SuggestionStatus.Applied"/> is a conflict.
    /// </summary>
    public Task<(ReviewResult Result, SuggestionDto? Suggestion)> DecideAsync(Guid id, DecisionOutcome outcome, CancellationToken ct) =>
        ChangeOneAsync(id, s => s.Status != ToStatus(outcome), (_, _, _) => ReviewResult.Ok, outcome, ct);

    /// <summary>
    /// Changes the outcome, marks the suggestion edited and approves it; allowed in any status but applied.
    /// <c>IsNewLabel</c> follows the Gmail label list; without a Gmail connection a changed label counts as new.
    /// <paramref name="replaceLabels"/> (null: unchanged) are current labels of the message, by name; one it does not
    /// carry is <see cref="ReviewResult.InvalidReplaceLabels"/> with the names it does not carry in <c>Unknown</c>,
    /// no label list <see cref="ReviewResult.LabelsUnavailable"/>.
    /// </summary>
    public async Task<(ReviewResult Result, SuggestionDto? Suggestion, IReadOnlyList<string> Unknown)> EditAsync(
        Guid id, string topicLabel, bool needsAction, bool toBeDeleted, IReadOnlyList<string>? replaceLabels, CancellationToken ct)
    {
        var isNewLabel = await IsNewLabelAsync(topicLabel, ct);
        IReadOnlyList<string> unknown = [];
        var (result, suggestion) = await ChangeOneAsync(id, _ => true, (s, m, personal) =>
        {
            IReadOnlyList<(string Id, string Name)>? replaced = null;
            if (replaceLabels is { Count: > 0 })
            {
                if (personal.IsUnavailable)
                {
                    return ReviewResult.LabelsUnavailable;
                }

                var current = personal.NamesOf(m).Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                unknown = Unknown(replaceLabels, current);
                if (unknown.Count > 0)
                {
                    return ReviewResult.InvalidReplaceLabels;
                }

                replaced = personal.Carried(m, replaceLabels, topicLabel);
            }

            s.IsNewLabel = isNewLabel ?? (s.IsNewLabel || !string.Equals(s.TopicLabel, topicLabel, StringComparison.OrdinalIgnoreCase));
            s.TopicLabel = topicLabel;
            s.NeedsAction = needsAction;
            s.ToBeDeleted = toBeDeleted;
            if (replaceLabels is not null)
            {
                s.SetReplaced(replaced ?? []);
            }

            s.Edited = true;
            return ReviewResult.Ok;
        }, DecisionOutcome.Approved, ct);
        return (result, suggestion, unknown);
    }

    /// <summary>
    /// Approves the pending members with the card's outcome (<see cref="DecideGroupAsync"/>). With
    /// <see cref="GroupOutcome.ReplaceLabels"/>: a name no pending member carries is
    /// <see cref="ReviewResult.InvalidReplaceLabels"/> with those names in <c>Unknown</c>, no label list
    /// <see cref="ReviewResult.LabelsUnavailable"/>.
    /// </summary>
    public async Task<(ReviewResult Result, GroupDecisionResponse? Response, IReadOnlyList<string> Unknown)> ApproveGroupAsync(
        string senderAddress, string groupKey, GroupOutcome shown, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string>? names = null;
        if (shown.ReplaceLabels is { Count: > 0 } replace)
        {
            var personal = await PersonalLabelsAsync(ct);
            if (personal.IsUnavailable)
            {
                return (ReviewResult.LabelsUnavailable, null, []);
            }

            var labelIds = await db.Suggestions.AsNoTracking()
                .Where(s => s.SenderAddress == senderAddress && s.GroupKey == groupKey && s.Status == SuggestionStatus.Pending)
                .Join(db.Messages.AsNoTracking(), s => s.MessageId, m => m.Id, (_, m) => m.LabelIds)
                .ToListAsync(ct);
            var carried = labelIds.SelectMany(ids => ids)
                .Select(id => personal.IsPersonal(id) ? personal.Names.GetValueOrDefault(id) : null)
                .OfType<string>()
                .Select(n => n.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = labelIds.Count > 0 ? Unknown(replace, carried) : [];
            if (unknown.Count > 0)
            {
                return (ReviewResult.InvalidReplaceLabels, null, unknown);
            }

            names = personal.Names;
        }

        return (ReviewResult.Ok, await DecideGroupAsync(senderAddress, groupKey, DecisionOutcome.Approved, shown, names, ct), []);
    }

    /// <summary>The trimmed names of <paramref name="replaceLabels"/> not in <paramref name="carried"/>, distinct, in request order.</summary>
    private static IReadOnlyList<string> Unknown(IReadOnlyList<string> replaceLabels, HashSet<string> carried) =>
        [.. replaceLabels.Select(l => l.Trim()).Where(l => !carried.Contains(l)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Rejects every pending member of the sender's group, or approves those whose outcome is <paramref name="shown"/>
    /// (the card's). A to-be-deleted suggestion of a protected message is only ever approved on its own.
    /// </summary>
    public Task<GroupDecisionResponse> DecideGroupAsync(
        string senderAddress, string groupKey, DecisionOutcome outcome, GroupOutcome? shown, CancellationToken ct) =>
        DecideGroupAsync(senderAddress, groupKey, outcome, shown, null, ct);

    /// <param name="names">Current personal label names by id, to match <see cref="GroupOutcome.ReplaceLabels"/>.</param>
    private async Task<GroupDecisionResponse> DecideGroupAsync(
        string senderAddress, string groupKey, DecisionOutcome outcome, GroupOutcome? shown,
        IReadOnlyDictionary<string, string>? names, CancellationToken ct)
    {
        if (outcome == DecisionOutcome.Approved)
        {
            ArgumentNullException.ThrowIfNull(shown);
        }

        var (allowlist, rules) = await ProtectionAsync(ct);
        var candidates = db.Suggestions.AsNoTracking().Where(s =>
            s.SenderAddress == senderAddress && s.GroupKey == groupKey && s.Status == SuggestionStatus.Pending);
        var changed = 0;
        var skipped = new List<Guid>();
        await foreach (var chunk in ChunksAsync(candidates, ct))
        {
            changed += await ChangeChunkAsync(chunk, outcome, (s, m) =>
            {
                if (outcome == DecisionOutcome.Rejected
                    || (s.TopicLabel == shown!.TopicLabel && s.NeedsAction == shown.NeedsAction && s.ToBeDeleted == shown.ToBeDeleted
                        && !(s.ToBeDeleted && MessageProtection.IsProtected(m, allowlist, rules))))
                {
                    if (outcome == DecisionOutcome.Approved)
                    {
                        KeepReplaced(s, shown!.ReplaceLabels, names);
                    }

                    return true;
                }

                if (skipped.Count < MaxSkippedIds)
                {
                    skipped.Add(s.Id);
                }

                return false;
            }, ct);
        }

        return new GroupDecisionResponse(changed, skipped);
    }

    /// <summary>
    /// Edits every pending member of the sender's group to <paramref name="outcome"/> and approves it (one decision row
    /// each), as <see cref="EditAsync"/> does for one; a protected message never gets a to-be-deleted outcome here.
    /// </summary>
    public async Task<GroupDecisionResponse> EditGroupAsync(string senderAddress, string groupKey, GroupOutcome outcome, CancellationToken ct)
    {
        var skipped = new List<Guid>();
        var edit = await EditToAsync(outcome, skipped, ct);
        var candidates = db.Suggestions.AsNoTracking().Where(s =>
            s.SenderAddress == senderAddress && s.GroupKey == groupKey && s.Status == SuggestionStatus.Pending);
        var changed = 0;
        await foreach (var chunk in ChunksAsync(candidates, ct))
        {
            changed += await ChangeChunkAsync(chunk, DecisionOutcome.Approved, edit, ct);
        }

        return new GroupDecisionResponse(changed, skipped);
    }

    /// <summary>
    /// Approves the suggestion only while it is pending, edited to <paramref name="edit"/> first when given; a protected
    /// message never gets a to-be-deleted edit (it is skipped). Joins the caller's transaction when there is one.
    /// </summary>
    public async Task<GroupDecisionResponse> ApprovePendingAsync(Guid id, GroupOutcome? edit, CancellationToken ct)
    {
        var skipped = new List<Guid>();
        Func<SuggestionRow, MessageRow, bool> include = edit is null ? (_, _) => true : await EditToAsync(edit, skipped, ct);
        return new GroupDecisionResponse(await ChangeChunkAsync([id], DecisionOutcome.Approved, include, ct), skipped);
    }

    /// <summary>
    /// Approves pending suggestions with <c>confidence ≥ threshold</c>: model answers only unless
    /// <paramref name="includeDerived"/>; a to-be-deleted suggestion of a protected message stays pending (only an
    /// individual approve takes it).
    /// </summary>
    public async Task<BulkApproveResponse> BulkApproveAsync(
        double? threshold, bool includeDerived, string? senderAddress, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var min = threshold ?? settings.BulkApproveThreshold;
        SuggestionSource[] sources = includeDerived
            ? [SuggestionSource.Llm, SuggestionSource.Derived, SuggestionSource.Memory]
            : [SuggestionSource.Llm];
        var candidates = db.Suggestions.AsNoTracking().Where(s =>
            s.Status == SuggestionStatus.Pending && s.Confidence >= min && sources.Contains(s.Source));
        if (senderAddress is not null)
        {
            candidates = candidates.Where(s => s.SenderAddress == senderAddress);
        }

        var allowlist = await AllowlistLoader.LoadAsync(db, settings, ct);
        var approved = 0;
        var skipped = 0;
        var skippedIds = new List<Guid>();
        await foreach (var chunk in ChunksAsync(candidates, ct))
        {
            approved += await ChangeChunkAsync(chunk, DecisionOutcome.Approved, (s, m) =>
            {
                if (s.ToBeDeleted && MessageProtection.IsProtected(m, allowlist, settings.Protection))
                {
                    if (skipped++ < MaxSkippedIds)
                    {
                        skippedIds.Add(s.Id);
                    }

                    return false;
                }

                return true;
            }, ct);
        }

        return new BulkApproveResponse(approved, skipped, skippedIds);
    }

    /// <summary>
    /// Resets the messages of the pending or rejected suggestions to not analysed (the re-analyse path) and queues one
    /// <see cref="AnalysisScope.Messages"/> run without grouping.
    /// </summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is selected; nothing is reset.</exception>
    public async Task<(ReviewResult Result, AnalysisRunDto? Run)> AnalyseIndividuallyAsync(Guid[] suggestionIds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace((await settingsStore.GetAsync(ct)).ChatModel))
        {
            throw new LlmNotConfiguredException(ModelKinds.Chat);
        }

        var found = await db.Suggestions.AsNoTracking()
            .Where(s => suggestionIds.Contains(s.Id))
            .Select(s => new { s.MessageId, s.Status })
            .ToListAsync(ct);
        if (found.Count == 0)
        {
            return (ReviewResult.NotFound, null);
        }

        var messageIds = found
            .Where(s => s.Status is SuggestionStatus.Pending or SuggestionStatus.Rejected)
            .Select(s => s.MessageId)
            .ToArray();
        if (messageIds.Length == 0 || (await runs.ReanalyseAsync(messageIds, null, ct)).Result == ReanalyseResult.OnlyDecided)
        {
            return (ReviewResult.Conflict, null);
        }

        var run = await runs.StartAsync(AnalysisScope.Messages, null, messageIds, messageIds.Length, AnalysisGroupingMode.Off, ct);
        return (ReviewResult.Ok, run);
    }

    /// <summary>Whether Gmail lacks the label; null without a Gmail connection or when the label list cannot be loaded.</summary>
    private async Task<bool?> IsNewLabelAsync(string topicLabel, CancellationToken ct)
    {
        try
        {
            return await labels.FindByNameAsync(topicLabel, ct) is null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The edit for <see cref="ChangeChunkAsync"/>: skips (and lists) a protected message the outcome would delete.</summary>
    private async Task<Func<SuggestionRow, MessageRow, bool>> EditToAsync(
        GroupOutcome outcome, List<Guid> skipped, CancellationToken ct)
    {
        var isNewLabel = await IsNewLabelAsync(outcome.TopicLabel, ct);
        var (allowlist, rules) = await ProtectionAsync(ct);
        var names = outcome.ReplaceLabels is { Count: > 0 } ? (await PersonalLabelsAsync(ct)).Names : null;
        return (s, m) =>
        {
            if (outcome.ToBeDeleted && MessageProtection.IsProtected(m, allowlist, rules))
            {
                if (skipped.Count < MaxSkippedIds)
                {
                    skipped.Add(s.Id);
                }

                return false;
            }

            s.IsNewLabel = isNewLabel ?? (s.IsNewLabel || !string.Equals(s.TopicLabel, outcome.TopicLabel, StringComparison.OrdinalIgnoreCase));
            s.TopicLabel = outcome.TopicLabel;
            s.NeedsAction = outcome.NeedsAction;
            s.ToBeDeleted = outcome.ToBeDeleted;
            KeepReplaced(s, outcome.ReplaceLabels, names);
            s.Edited = true;
            return true;
        };
    }

    /// <summary>
    /// Keeps the suggestion's own replaced labels whose name (current, else as stored) is in <paramref name="requested"/>;
    /// never adds one. Marks the suggestion edited when that drops any. Null leaves them.
    /// </summary>
    private static void KeepReplaced(SuggestionRow s, IReadOnlyList<string>? requested, IReadOnlyDictionary<string, string>? names)
    {
        if (requested is null)
        {
            return;
        }

        var wanted = requested.Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (s.SetReplaced([.. s.Replaced(names).Where(l => wanted.Contains(l.Name.Trim()))]))
        {
            s.Edited = true;
        }
    }

    /// <summary>The personal labels by id; <see cref="PersonalLabels.None"/> (no names) when Gmail is not reachable.</summary>
    private async Task<PersonalLabels> PersonalLabelsAsync(CancellationToken ct) =>
        await PersonalLabels.LoadAsync(labels, await settingsStore.GetAsync(ct), ct);

    /// <summary>The allowlist and the protection rules, from one settings read.</summary>
    private async Task<(Allowlist Allowlist, ProtectionSettings Rules)> ProtectionAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        return (await AllowlistLoader.LoadAsync(db, settings, ct), settings.Protection);
    }

    private static SuggestionStatus ToStatus(DecisionOutcome outcome) =>
        outcome == DecisionOutcome.Approved ? SuggestionStatus.Approved : SuggestionStatus.Rejected;

    private async Task<(ReviewResult Result, SuggestionDto? Suggestion)> ChangeOneAsync(
        Guid id,
        Func<SuggestionRow, bool> needsChange,
        Func<SuggestionRow, MessageRow, PersonalLabels, ReviewResult> change,
        DecisionOutcome outcome,
        CancellationToken ct)
    {
        // One settings and label list load per request: the change and the returned DTO share them.
        var settings = await settingsStore.GetAsync(ct);
        var personal = await PersonalLabels.LoadAsync(labels, settings, ct);
        var names = personal.IsUnavailable ? null : personal.Names;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var suggestion = (await LockAsync([id], ct)).SingleOrDefault();
        if (suggestion is null)
        {
            return (ReviewResult.NotFound, null);
        }

        var message = await db.Messages.SingleAsync(m => m.Id == suggestion.MessageId, ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, ct);
        var rules = settings.Protection;
        if (suggestion.Status == SuggestionStatus.Applied)
        {
            return (ReviewResult.Conflict, ReviewQuery.ToDto(suggestion, message, allowlist, rules, names));
        }

        if (needsChange(suggestion))
        {
            var result = change(suggestion, message, personal);
            if (result != ReviewResult.Ok)
            {
                // Nothing was changed yet; the transaction rolls back on dispose.
                return (result, null);
            }

            suggestion.SetStatus(ToStatus(outcome), message, time.GetUtcNow());
            await decisions.RecordAsync(suggestion, message, outcome, ct);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        decisions.Committed();
        return (ReviewResult.Ok, ReviewQuery.ToDto(suggestion, message, allowlist, rules, names));
    }

    /// <summary>
    /// Changes the chunk's rows that are still pending under the lock and that <paramref name="include"/> accepts. Inside
    /// a caller's transaction (accepting a Claude verdict) it joins it, and the caller commits and calls
    /// <see cref="DecisionRecorder.Committed"/>.
    /// </summary>
    private async Task<int> ChangeChunkAsync(
        Guid[] ids, DecisionOutcome outcome, Func<SuggestionRow, MessageRow, bool> include, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var locked = (await LockAsync(ids, ct)).Where(s => s.Status == SuggestionStatus.Pending).ToList();
        var messageIds = locked.ConvertAll(s => s.MessageId);
        var messages = await db.Messages.Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        var now = time.GetUtcNow();
        var changed = 0;
        foreach (var suggestion in locked)
        {
            var message = messages[suggestion.MessageId];
            if (!include(suggestion, message))
            {
                continue;
            }

            suggestion.SetStatus(ToStatus(outcome), message, now);
            await decisions.RecordAsync(suggestion, message, outcome, ct);
            changed++;
        }

        await db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
            decisions.Committed();
        }

        db.ChangeTracker.Clear();
        return changed;
    }

    /// <summary>The matching ids in keyset chunks of <see cref="ChunkSize"/>; rows left pending are not seen twice.</summary>
    private static async IAsyncEnumerable<Guid[]> ChunksAsync(
        IQueryable<SuggestionRow> candidates, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Guid? after = null;
        while (true)
        {
            var page = after is { } last ? candidates.Where(s => s.Id.CompareTo(last) > 0) : candidates;
            var ids = await page.OrderBy(s => s.Id).Select(s => s.Id).Take(ChunkSize).ToArrayAsync(ct);
            if (ids.Length == 0)
            {
                yield break;
            }

            yield return ids;
            after = ids[^1];
        }
    }

    /// <summary>Loads and locks the rows until the transaction ends; uncomposed so the lock clause stays at the top level.</summary>
    private Task<List<SuggestionRow>> LockAsync(Guid[] ids, CancellationToken ct) =>
        db.Suggestions.FromSql($"SELECT * FROM suggestions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE").ToListAsync(ct);
}
