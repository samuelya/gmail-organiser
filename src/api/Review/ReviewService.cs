using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>The outcome a group card shows: label, needs-action, to-be-deleted and document type.</summary>
/// <param name="ReplaceLabels">
/// Each member keeps only its own replaced labels named here (never gains one; empty clears them); null leaves them.
/// </param>
/// <param name="DocumentTypeLabel">The card's document-type label; null matches only members without one.</param>
public sealed record GroupOutcome(
    string TopicLabel, bool NeedsAction, bool ToBeDeleted, IReadOnlyList<string>? ReplaceLabels = null, string? DocumentTypeLabel = null);

/// <summary>
/// An edit of a group's members (or one suggestion) to an outcome. Unlike <see cref="GroupOutcome"/>, which matches
/// members by their document type, <paramref name="DocumentType"/> says what the edit does to it.
/// </summary>
/// <param name="ReplaceLabels">As in <see cref="GroupOutcome"/>.</param>
public sealed record GroupEdit(
    string TopicLabel, bool NeedsAction, bool ToBeDeleted, IReadOnlyList<string>? ReplaceLabels, DocumentTypeChange DocumentType);

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

    /// <summary>The edit would make the topic label the same as the document-type label it keeps.</summary>
    DocumentTypeIsTopic,
}

/// <summary>
/// Review decisions. Every change locks its suggestion rows (<c>FOR UPDATE</c>) and re-checks the status under the
/// lock, then changes the status through <see cref="SuggestionRow.SetStatus"/> and records one decision in the same
/// transaction, so a concurrent apply is never overwritten. Nothing here touches Gmail. Endpoints validate first.
/// </summary>
public sealed partial class ReviewService(
    AppDbContext db,
    DecisionRecorder decisions,
    AnalysisRunService runs,
    LabelCatalog labels,
    ISettingsStore settingsStore,
    TimeProvider time,
    ILogger<ReviewService> logger)
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
    /// <c>IsNewLabel</c> and <c>DocumentTypeIsNew</c> follow the Gmail label list; without a Gmail connection a changed
    /// label counts as new. <paramref name="documentType"/> is <see cref="DocumentTypeEdit.Validate"/>d and stored in
    /// Gmail's spelling; a topic label equal to the document type the suggestion keeps is
    /// <see cref="ReviewResult.DocumentTypeIsTopic"/>.
    /// <paramref name="replaceLabels"/> (null: unchanged) are current labels of the message, by name; one it does not
    /// carry is <see cref="ReviewResult.InvalidReplaceLabels"/> with the names it does not carry in <c>Unknown</c>,
    /// no label list <see cref="ReviewResult.LabelsUnavailable"/>. Replaced action and delete labels stay
    /// (<see cref="ReplacedLabels"/>).
    /// </summary>
    public async Task<(ReviewResult Result, SuggestionDto? Suggestion, IReadOnlyList<string> Unknown)> EditAsync(
        Guid id, string topicLabel, bool needsAction, bool toBeDeleted, IReadOnlyList<string>? replaceLabels,
        DocumentTypeChange documentType, MailTypeChange mailType, CancellationToken ct)
    {
        var isNewLabel = await IsNewLabelAsync(topicLabel, ct);
        var (type, typeIsNew) = await DocumentTypeEdit.ResolveAsync(labels, documentType, ct);
        var settings = await settingsStore.GetAsync(ct);
        IReadOnlyList<string> unknown = [];
        var (result, suggestion) = await ChangeOneAsync(id, _ => true, (s, m, personal) =>
        {
            if (DocumentTypeEdit.IsTopic(type.IsSet ? type.Label : s.DocumentTypeLabel, topicLabel))
            {
                return ReviewResult.DocumentTypeIsTopic;
            }

            IReadOnlyList<(string Id, string Name)>? replaced = null;
            if (replaceLabels is { Count: > 0 })
            {
                if (personal.IsUnavailable)
                {
                    return ReviewResult.LabelsUnavailable;
                }

                var current = personal.NamesOf(m).Concat(ReplacedLabels.AppOf(s, settings).Select(l => l.Name))
                    .Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                unknown = ReplacedLabels.Unknown(replaceLabels, current);
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
            DocumentTypeEdit.Apply(s, type, typeIsNew);
            if (mailType.IsSet)
            {
                s.MailType = mailType.Value;
            }

            if (replaceLabels is not null)
            {
                s.SetReplaced([.. (replaced ?? []).Concat(ReplacedLabels.AppOf(s, settings)).DistinctBy(l => l.Id, StringComparer.Ordinal)]);
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
            var settings = await settingsStore.GetAsync(ct);
            var personal = await PersonalLabels.LoadAsync(labels, settings, ct);
            if (personal.IsUnavailable)
            {
                return (ReviewResult.LabelsUnavailable, null, []);
            }

            var members = await db.Suggestions.AsNoTracking()
                .Where(s => s.SenderAddress == senderAddress && s.GroupKey == groupKey && s.Status == SuggestionStatus.Pending)
                .Join(db.Messages.AsNoTracking(), s => s.MessageId, m => m.Id, (s, m) => new { S = s, m.LabelIds })
                .ToListAsync(ct);
            var carried = members.SelectMany(x => x.LabelIds)
                .Select(id => personal.IsPersonal(id) ? personal.Names.GetValueOrDefault(id) : null)
                .OfType<string>()
                .Concat(members.SelectMany(x => ReplacedLabels.AppOf(x.S, settings)).Select(l => l.Name))
                .Select(n => n.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = members.Count > 0 ? ReplacedLabels.Unknown(replace, carried) : [];
            if (unknown.Count > 0)
            {
                return (ReviewResult.InvalidReplaceLabels, null, unknown);
            }

            names = personal.Names;
        }

        return (ReviewResult.Ok, await DecideGroupAsync(senderAddress, groupKey, DecisionOutcome.Approved, shown, names, ct), []);
    }

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

        var (allowlist, rules, settings) = await ProtectionAsync([senderAddress], ct);
        var candidates = db.Suggestions.AsNoTracking().Where(s =>
            s.SenderAddress == senderAddress && s.GroupKey == groupKey && s.Status == SuggestionStatus.Pending);
        var changed = 0;
        var skipped = new List<Guid>();
        await foreach (var chunk in GroupChunksAsync(candidates, ct))
        {
            changed += await ChangeChunkAsync(chunk, outcome, (s, m) =>
            {
                if (outcome == DecisionOutcome.Rejected
                    || (s.TopicLabel == shown!.TopicLabel && s.NeedsAction == shown.NeedsAction && s.ToBeDeleted == shown.ToBeDeleted
                        && s.DocumentTypeLabel == shown.DocumentTypeLabel
                        && !(s.ToBeDeleted && MessageProtection.IsProtected(m, allowlist, rules))))
                {
                    if (outcome == DecisionOutcome.Approved)
                    {
                        ReplacedLabels.Keep(s, shown!.ReplaceLabels, names, settings);
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
    public async Task<GroupDecisionResponse> EditGroupAsync(string senderAddress, string groupKey, GroupEdit outcome, CancellationToken ct)
    {
        var skipped = new List<Guid>();
        var edit = await EditToAsync(outcome, [senderAddress], skipped, ct);
        var candidates = db.Suggestions.AsNoTracking().Where(s =>
            s.SenderAddress == senderAddress && s.GroupKey == groupKey && s.Status == SuggestionStatus.Pending);
        var changed = 0;
        await foreach (var chunk in GroupChunksAsync(candidates, ct))
        {
            changed += await ChangeChunkAsync(chunk, DecisionOutcome.Approved, edit, ct);
        }

        return new GroupDecisionResponse(changed, skipped);
    }

    /// <summary>
    /// Approves the suggestion only while it is pending, edited to <paramref name="edit"/> first when given; a protected
    /// message never gets a to-be-deleted edit (it is skipped). Joins the caller's transaction when there is one.
    /// </summary>
    public async Task<GroupDecisionResponse> ApprovePendingAsync(Guid id, GroupEdit? edit, CancellationToken ct)
    {
        var skipped = new List<Guid>();
        Func<SuggestionRow, MessageRow, bool> include = edit is null
            ? (_, _) => true
            : await EditToAsync(edit, await db.Suggestions.Where(s => s.Id == id).Select(s => s.SenderAddress).ToListAsync(ct), skipped, ct);
        return new GroupDecisionResponse(await ChangeChunkAsync([id], DecisionOutcome.Approved, include, ct), skipped);
    }

    /// <summary>
    /// Approves pending suggestions with <c>confidence ≥ threshold</c>: model answers only unless
    /// <paramref name="includeDerived"/> (derived, memory and Stage-0 ones too); a to-be-deleted suggestion of a protected message stays pending (only an
    /// individual approve takes it). With the taxonomy locked (#367), a suggestion whose label Gmail still lacks stays pending
    /// too until it is approved individually or edited to an existing label; the response counts those.
    /// </summary>
    public async Task<BulkApproveResponse> BulkApproveAsync(
        double? threshold, bool includeDerived, string? senderAddress, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var min = threshold ?? settings.BulkApproveThreshold;
        SuggestionSource[] sources = includeDerived
            ? [SuggestionSource.Llm, SuggestionSource.Derived, SuggestionSource.Memory, SuggestionSource.Stage0]
            : [SuggestionSource.Llm];
        var candidates = db.Suggestions.AsNoTracking().Where(s =>
            s.Status == SuggestionStatus.Pending && s.Confidence >= min && sources.Contains(s.Source));
        if (senderAddress is not null)
        {
            candidates = candidates.Where(s => s.SenderAddress == senderAddress);
        }

        var excluded = 0;
        if (settings.TaxonomyLocked)
        {
            // New is decided now, not at analysis: a label created in Gmail since then is an existing label.
            var personal = await PersonalLabels.LoadAsync(labels, settings, ct);
            var kept = candidates.Where(ReviewQuery.NotStillNew(personal.IsUnavailable ? null : personal.Names));
            excluded = await candidates.CountAsync(ct) - await kept.CountAsync(ct);
            candidates = kept;
        }

        var approved = 0;
        var skipped = 0;
        var skippedIds = new List<Guid>();
        await foreach (var chunk in ChunksAsync(candidates, ct))
        {
            // Per chunk, so a sender allowlisted during a long bulk approve is honoured for the chunks that follow.
            var senders = await db.Suggestions.Where(s => chunk.Contains(s.Id)).Select(s => s.SenderAddress).Distinct().ToListAsync(ct);
            var allowlist = await AllowlistLoader.LoadAsync(db, settings, senders, ct);
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

        return new BulkApproveResponse(approved, skipped, skippedIds, excluded);
    }

    /// <summary>
    /// Resets the messages of the pending or rejected suggestions to not analysed (the re-analyse path) and queues one
    /// <see cref="AnalysisScope.Messages"/> run without grouping.
    /// </summary>
    /// <exception cref="LlmNotConfiguredException">No chat model is selected, or no usable Claude API key is set; nothing is reset.</exception>
    public async Task<(ReviewResult Result, AnalysisRunDto? Run)> AnalyseIndividuallyAsync(Guid[] suggestionIds, CancellationToken ct)
    {
        var settings = await runs.RequireChatModelAsync(ct);

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

        var run = await runs.StartAsync(
            settings, AnalysisScope.Messages, null, messageIds, messageIds.Length, AnalysisGroupingMode.Off, ct);
        return (ReviewResult.Ok, run);
    }

    /// <summary>
    /// The edit for <see cref="ChangeChunkAsync"/>: skips (and lists) a protected message the outcome would delete. A
    /// member's document type equal to the new topic label is dropped with a warning (the callers are Claude verdicts).
    /// </summary>
    private async Task<Func<SuggestionRow, MessageRow, bool>> EditToAsync(
        GroupEdit outcome, IReadOnlyCollection<string> senders, List<Guid> skipped, CancellationToken ct)
    {
        var isNewLabel = await IsNewLabelAsync(outcome.TopicLabel, ct);
        var (type, typeIsNew) = await DocumentTypeEdit.ResolveAsync(labels, outcome.DocumentType, ct);
        var (allowlist, rules, settings) = await ProtectionAsync(senders, ct);
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
            DocumentTypeEdit.Apply(s, type, typeIsNew);
            if (DocumentTypeEdit.IsTopic(s.DocumentTypeLabel, outcome.TopicLabel))
            {
                LogDocumentTypeDropped(logger, s.Id);
                DocumentTypeEdit.Apply(s, DocumentTypeChange.Clear, null);
            }

            ReplacedLabels.Keep(s, outcome.ReplaceLabels, names, settings);
            s.Edited = true;
            return true;
        };
    }

    /// <summary>The personal labels by id; <see cref="PersonalLabels.None"/> (no names) when Gmail is not reachable.</summary>
    private async Task<PersonalLabels> PersonalLabelsAsync(CancellationToken ct) =>
        await PersonalLabels.LoadAsync(labels, await settingsStore.GetAsync(ct), ct);

    /// <summary>The allowlist for <paramref name="senders"/>, the protection rules and the settings, from one settings read.</summary>
    private async Task<(Allowlist Allowlist, ProtectionSettings Rules, AppSettings Settings)> ProtectionAsync(
        IReadOnlyCollection<string> senders, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        return (await AllowlistLoader.LoadAsync(db, settings, senders, ct), settings.Protection, settings);
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
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, [message.FromAddress], ct);
        var rules = settings.Protection;
        if (suggestion.Status == SuggestionStatus.Applied)
        {
            return (ReviewResult.Conflict, ReviewQuery.ToDto(suggestion, message, allowlist, rules, names, personal.AppLabelIds, settings.TaxonomyLocked));
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
        return (ReviewResult.Ok, ReviewQuery.ToDto(suggestion, message, allowlist, rules, names, personal.AppLabelIds, settings.TaxonomyLocked));
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
        await decisions.LockPoliciesAsync(locked.Select(s => (s, messages[s.MessageId])), outcome, ct);
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
}
