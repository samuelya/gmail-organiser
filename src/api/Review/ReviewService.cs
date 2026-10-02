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
public sealed record GroupOutcome(string TopicLabel, bool NeedsAction, bool ToBeDeleted);

public enum ReviewResult
{
    Ok,
    NotFound,

    /// <summary>Already applied (or, for analyse individually, every suggestion is approved or applied).</summary>
    Conflict,
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
        ChangeOneAsync(id, s => s.Status != ToStatus(outcome), _ => { }, outcome, ct);

    /// <summary>
    /// Changes the outcome, marks the suggestion edited and approves it; allowed in any status but applied.
    /// <c>IsNewLabel</c> follows the Gmail label list; without a Gmail connection a changed label counts as new.
    /// </summary>
    public async Task<(ReviewResult Result, SuggestionDto? Suggestion)> EditAsync(
        Guid id, string topicLabel, bool needsAction, bool toBeDeleted, CancellationToken ct)
    {
        bool? isNewLabel;
        try
        {
            isNewLabel = await labels.FindByNameAsync(topicLabel, ct) is null;
        }
        catch (GmailNotConnectedException)
        {
            isNewLabel = null;
        }

        return await ChangeOneAsync(id, _ => true, s =>
        {
            s.IsNewLabel = isNewLabel ?? (s.IsNewLabel || !string.Equals(s.TopicLabel, topicLabel, StringComparison.OrdinalIgnoreCase));
            s.TopicLabel = topicLabel;
            s.NeedsAction = needsAction;
            s.ToBeDeleted = toBeDeleted;
            s.Edited = true;
        }, DecisionOutcome.Approved, ct);
    }

    /// <summary>
    /// Rejects every pending member of the sender's group, or approves those whose outcome is <paramref name="shown"/>
    /// (the card's). A to-be-deleted suggestion of a protected message is only ever approved on its own.
    /// </summary>
    public async Task<GroupDecisionResponse> DecideGroupAsync(
        string senderAddress, string groupKey, DecisionOutcome outcome, GroupOutcome? shown, CancellationToken ct)
    {
        if (outcome == DecisionOutcome.Approved)
        {
            ArgumentNullException.ThrowIfNull(shown);
        }

        var allowlisted = await db.Senders.AnyAsync(s => s.Address == senderAddress && s.Allowlisted, ct);
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
                        && !(s.ToBeDeleted && MessageProtection.IsProtected(m, allowlisted))))
                {
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
    /// Approves pending suggestions with <c>confidence ≥ threshold</c>: model answers only unless
    /// <paramref name="includeDerived"/>; a to-be-deleted suggestion of a protected message stays pending (only an
    /// individual approve takes it).
    /// </summary>
    public async Task<BulkApproveResponse> BulkApproveAsync(
        double? threshold, bool includeDerived, string? senderAddress, CancellationToken ct)
    {
        var min = threshold ?? (await settingsStore.GetAsync(ct)).BulkApproveThreshold;
        SuggestionSource[] sources = includeDerived
            ? [SuggestionSource.Llm, SuggestionSource.Derived, SuggestionSource.Memory]
            : [SuggestionSource.Llm];
        var candidates = db.Suggestions.AsNoTracking().Where(s =>
            s.Status == SuggestionStatus.Pending && s.Confidence >= min && sources.Contains(s.Source));
        if (senderAddress is not null)
        {
            candidates = candidates.Where(s => s.SenderAddress == senderAddress);
        }

        var approved = 0;
        var skipped = 0;
        var skippedIds = new List<Guid>();
        await foreach (var chunk in ChunksAsync(candidates, ct))
        {
            var allowlisted = (await db.Senders.AsNoTracking()
                    .Where(s => s.Allowlisted && db.Suggestions.Any(x => chunk.Contains(x.Id) && x.SenderAddress == s.Address))
                    .Select(s => s.Address)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
            approved += await ChangeChunkAsync(chunk, DecisionOutcome.Approved, (s, m) =>
            {
                if (s.ToBeDeleted && MessageProtection.IsProtected(m, allowlisted))
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

    private static SuggestionStatus ToStatus(DecisionOutcome outcome) =>
        outcome == DecisionOutcome.Approved ? SuggestionStatus.Approved : SuggestionStatus.Rejected;

    private async Task<(ReviewResult Result, SuggestionDto? Suggestion)> ChangeOneAsync(
        Guid id, Func<SuggestionRow, bool> needsChange, Action<SuggestionRow> change, DecisionOutcome outcome, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var suggestion = (await LockAsync([id], ct)).SingleOrDefault();
        if (suggestion is null)
        {
            return (ReviewResult.NotFound, null);
        }

        var message = await db.Messages.SingleAsync(m => m.Id == suggestion.MessageId, ct);
        var allowlisted = await db.Senders.AnyAsync(s => s.Address == message.FromAddress && s.Allowlisted, ct);
        if (suggestion.Status == SuggestionStatus.Applied)
        {
            return (ReviewResult.Conflict, ReviewQuery.ToDto(suggestion, message, allowlisted));
        }

        if (needsChange(suggestion))
        {
            change(suggestion);
            suggestion.SetStatus(ToStatus(outcome), message, time.GetUtcNow());
            await decisions.RecordAsync(suggestion, message, outcome, ct);
            await db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (ReviewResult.Ok, ReviewQuery.ToDto(suggestion, message, allowlisted));
    }

    /// <summary>Changes the chunk's rows that are still pending under the lock and that <paramref name="include"/> accepts.</summary>
    private async Task<int> ChangeChunkAsync(
        Guid[] ids, DecisionOutcome outcome, Func<SuggestionRow, MessageRow, bool> include, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
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
        await tx.CommitAsync(ct);
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
