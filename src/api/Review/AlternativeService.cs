using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>
/// Accepts or discards compare-run alternatives (#249). Accept copies the alternative into its suggestion and makes it
/// pending again, whatever its status: an approval is withdrawn, and an applied suggestion keeps its <c>action_log</c>
/// rows (Undo of that batch still reverts the Gmail state it logged, but no longer touches the suggestion or the count)
/// and stops counting in <c>senders.applied_count</c>; the labels its applied outcome added that the new outcome doesn't add
/// join the replaced labels, so applying the new outcome removes them through the undo log (#310). Open Claude reviews of the suggestion or its group are closed, as they judged the old
/// outcome. Nothing changes in Gmail and no decision is recorded; the new outcome is approved and applied the normal
/// way. Both actions lock the suggestions <c>FOR UPDATE</c> by id in chunks of <see cref="ReviewService.ChunkSize"/>,
/// one transaction each, so they wait for each other and for a compare run's checkpoint (<c>FOR KEY SHARE</c>).
/// Endpoints validate first.
/// </summary>
public sealed class AlternativeService(
    AppDbContext db, ExternalReviewQuery externalReviews, IExternalReviewNotifier notifier, ISettingsStore settingsStore, TimeProvider time)
{
    /// <summary>Most suggestion ids and groups one request names, each.</summary>
    public const int MaxTargets = 1000;

    /// <summary>
    /// Accepts the targets' alternatives. Skipped: a suggestion in an active apply batch, or whose alternative or
    /// suggestion went before its chunk was locked (a concurrent discard, accept or re-analyse).
    /// </summary>
    public async Task<AlternativeDecisionResponse> AcceptAsync(Guid[] suggestionIds, GroupRef[] groups, CancellationToken ct)
    {
        var targets = await TargetsAsync(suggestionIds, groups, ct);
        var settings = await settingsStore.GetAsync(ct);
        int accepted = 0;
        var closed = new List<Guid>();
        foreach (var chunk in targets.Chunk(ReviewService.ChunkSize))
        {
            accepted += await AcceptChunkAsync(chunk, settings, closed, ct);
        }

        if (closed.Count > 0)
        {
            var rows = await db.ExternalReviews.AsNoTracking().Where(r => closed.Contains(r.Id)).ToListAsync(ct);
            foreach (var item in await externalReviews.ToDtosAsync(rows, ct))
            {
                await notifier.NotifyAsync(item, ct);
            }
        }

        return new AlternativeDecisionResponse(accepted, 0, targets.Length - accepted);
    }

    /// <summary>Deletes the alternatives; the suggestions are not touched. Skipped: an alternative that went meanwhile.</summary>
    public async Task<AlternativeDecisionResponse> DiscardAsync(Guid[] suggestionIds, GroupRef[] groups, CancellationToken ct)
    {
        var targets = await TargetsAsync(suggestionIds, groups, ct);
        var discarded = 0;
        foreach (var chunk in targets.Chunk(ReviewService.ChunkSize))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM suggestions WHERE id = ANY({chunk}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct);
            discarded += await db.SuggestionAlternatives.Where(a => chunk.Contains(a.SuggestionId)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        return new AlternativeDecisionResponse(0, discarded, targets.Length - discarded);
    }

    /// <summary>One transaction: accepts the locked chunk's alternatives; adds the Claude reviews it closed to <paramref name="closed"/>.</summary>
    private async Task<int> AcceptChunkAsync(Guid[] ids, AppSettings settings, List<Guid> closed, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.Suggestions.FromSql($"SELECT * FROM suggestions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        var alternatives = await db.SuggestionAlternatives.Where(a => ids.Contains(a.SuggestionId)).ToDictionaryAsync(a => a.SuggestionId, ct);
        var messageIds = locked.ConvertAll(s => s.MessageId);
        var messages = await db.Messages.Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        var inApply = await InApplyAsync(ids, ct);
        var appliedIds = locked.Where(s => s.Status == SuggestionStatus.Applied).Select(s => (Guid?)s.Id).ToArray();
        var logs = (await db.ActionLog.AsNoTracking()
                .Where(l => appliedIds.Contains(l.SuggestionId) && l.UndoneByBatchId == null)
                .ToListAsync(ct))
            .ToLookup(l => l.SuggestionId!.Value);
        var now = time.GetUtcNow();
        var done = new List<SuggestionRow>();
        var wasApplied = new List<string>();
        foreach (var suggestion in locked)
        {
            if (!alternatives.TryGetValue(suggestion.Id, out var alternative) || inApply(suggestion))
            {
                continue;
            }

            if (suggestion.Status == SuggestionStatus.Applied)
            {
                wasApplied.Add(suggestion.MessageId);
            }

            Copy(alternative, suggestion);
            var message = messages[suggestion.MessageId];
            if (logs[suggestion.Id].Any())
            {
                suggestion.SetReplaced([.. suggestion.Replaced(null)
                    .Concat(OldOutcomeLabels(logs[suggestion.Id], suggestion, message, settings))
                    .DistinctBy(l => l.Id, StringComparer.Ordinal)]);
            }

            suggestion.SetStatus(SuggestionStatus.Pending, message, now);
            db.SuggestionAlternatives.Remove(alternative);
            done.Add(suggestion);
        }

        await db.SaveChangesAsync(ct);
        // The message no longer counts as applied; re-applying the new outcome counts it again.
        var applied = wasApplied.ToArray();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE senders AS s SET applied_count = GREATEST(s.applied_count - c.n, 0)
            FROM (SELECT from_address, count(*)::int AS n FROM messages WHERE id = ANY({applied}) GROUP BY from_address) AS c
            WHERE s.address = c.from_address
            """, ct);
        closed.AddRange(await CloseClaudeReviewsAsync(done, now, ct));
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return done.Count;
    }

    /// <summary>
    /// The named suggestions and the groups' members in the group's status, that have an alternative; by id. A group's
    /// members are matched in one query over all the groups' senders and keys, then by exact (sender, key, status).
    /// </summary>
    private async Task<Guid[]> TargetsAsync(Guid[] suggestionIds, GroupRef[] groups, CancellationToken ct)
    {
        var named = suggestionIds.Distinct().ToArray();
        var ids = (await db.SuggestionAlternatives.AsNoTracking()
                .Where(a => named.Contains(a.SuggestionId))
                .Select(a => a.SuggestionId)
                .ToListAsync(ct))
            .ToHashSet();
        if (groups.Length > 0)
        {
            var wanted = groups.Select(g => (g.SenderAddress, g.GroupKey, ReviewQuery.ParseStatus(g.Status)!.Value)).ToHashSet();
            var senders = groups.Select(g => g.SenderAddress).Distinct().ToArray();
            var keys = groups.Select(g => g.GroupKey).Distinct().ToArray();
            var members = await db.Suggestions.AsNoTracking()
                .Where(s => senders.Contains(s.SenderAddress) && s.GroupKey != null && keys.Contains(s.GroupKey)
                    && db.SuggestionAlternatives.Any(a => a.SuggestionId == s.Id))
                .Select(s => new { s.Id, s.SenderAddress, s.GroupKey, s.Status })
                .ToListAsync(ct);
            ids.UnionWith(members.Where(m => wanted.Contains((m.SenderAddress, m.GroupKey!, m.Status))).Select(m => m.Id));
        }

        return [.. ids.Order()];
    }

    /// <summary>
    /// Closes the open Claude reviews of the accepted suggestions and of their groups (judged on the old outcome):
    /// queued or running ones are cancelled, reviewed ones dismissed. Returns their ids.
    /// </summary>
    private async Task<List<Guid>> CloseClaudeReviewsAsync(List<SuggestionRow> accepted, DateTimeOffset now, CancellationToken ct)
    {
        if (accepted.Count == 0)
        {
            return [];
        }

        var ids = accepted.ConvertAll(s => s.Id);
        var groups = accepted.Where(s => s.GroupKey is not null).Select(s => (s.SenderAddress, s.GroupKey!)).ToHashSet();
        var senders = groups.Select(g => g.SenderAddress).Distinct().ToArray();
        var keys = groups.Select(g => g.Item2).Distinct().ToArray();
        var open = (await db.ExternalReviews.AsNoTracking()
                .Where(r => (r.Status == ExternalReviewStatus.Queued || r.Status == ExternalReviewStatus.Running
                        || (r.Status == ExternalReviewStatus.Reviewed && r.Resolution == ExternalReviewResolution.None))
                    && ((r.TargetType == ExternalReviewTarget.Suggestion && r.SuggestionId != null && ids.Contains(r.SuggestionId.Value))
                        || (r.TargetType == ExternalReviewTarget.Group && senders.Contains(r.SenderAddress) && keys.Contains(r.GroupKey!))))
                .Select(r => new { r.Id, r.TargetType, r.SenderAddress, r.GroupKey })
                .ToListAsync(ct))
            .Where(r => r.TargetType == ExternalReviewTarget.Suggestion || groups.Contains((r.SenderAddress, r.GroupKey!)))
            .Select(r => r.Id)
            .ToArray();
        if (open.Length == 0)
        {
            return [];
        }

        await db.ExternalReviews
            .Where(r => open.Contains(r.Id) && (r.Status == ExternalReviewStatus.Queued || r.Status == ExternalReviewStatus.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ExternalReviewStatus.Cancelled), ct);
        await db.ExternalReviews
            .Where(r => open.Contains(r.Id) && r.Status == ExternalReviewStatus.Reviewed && r.Resolution == ExternalReviewResolution.None)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Resolution, ExternalReviewResolution.Dismissed)
                .SetProperty(r => r.ResolvedAt, now), ct);
        return [.. open];
    }

    /// <summary>
    /// Whether a queued, running or paused apply job covers the suggestion: approved and eligible by its cursor, or
    /// applied with log rows in its batch (a chunk whose send or revert may still change the suggestion).
    /// </summary>
    private async Task<Func<SuggestionRow, bool>> InApplyAsync(Guid[] ids, CancellationToken ct)
    {
        var cursors = (await db.Jobs.AsNoTracking()
                .Where(j => j.Type == ApplyActionsJob.JobType && JobRow.Active.Contains(j.Status) && j.Cursor != null)
                .Select(j => j.Cursor!)
                .ToListAsync(ct))
            .Select(c => JsonSerializer.Deserialize<ApplyCursor>(c, JobRow.Json)!)
            .ToList();
        if (cursors.Count == 0)
        {
            return _ => false;
        }

        var batchIds = cursors.ConvertAll(c => c.BatchId);
        var logged = (await db.ActionLog.AsNoTracking()
                .Where(l => batchIds.Contains(l.BatchId) && l.SuggestionId != null && ids.Contains(l.SuggestionId.Value))
                .Select(l => l.SuggestionId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        return s => s.Status switch
        {
            SuggestionStatus.Applied => logged.Contains(s.Id),
            SuggestionStatus.Approved => cursors.Any(c => s.DecidedAt <= c.ApprovedBefore
                && (c.SenderAddress is null || c.SenderAddress == s.SenderAddress)
                && (c.SuggestionIds is null || c.SuggestionIds.Contains(s.Id))),
            _ => false,
        };
    }

    /// <summary>
    /// The user labels the suggestion's not-undone apply rows added that the message still carries and the new outcome
    /// <paramref name="s"/> doesn't add (topic, applied document type, action and delete label, by name): the old
    /// outcome's labels apply removes. A log row's added names are aligned with its sorted added ids; the id stands in
    /// for a name the row doesn't have. Apply drops any it adds again.
    /// </summary>
    private static IEnumerable<(string Id, string Name)> OldOutcomeLabels(
        IEnumerable<ActionLogRow> rows, SuggestionRow s, MessageRow m, AppSettings settings)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s.TopicLabel.Trim() };
        if (s.DocumentTypeLabel is { } type && ActionPlanner.AppliesDocumentType(type, settings))
        {
            kept.Add(type.Trim());
        }

        if (s.NeedsAction)
        {
            kept.Add(settings.ActionLabelName.Trim());
        }

        if (s.ToBeDeleted)
        {
            kept.Add(settings.DeleteLabelName.Trim());
        }

        return rows
            .SelectMany(r =>
            {
                var added = LabelChunks.Sorted(r.LabelIdsAfter.Except(r.LabelIdsBefore, StringComparer.Ordinal));
                return added.Select((id, i) => (Id: id, Name: added.Length == r.LabelsAdded.Length ? r.LabelsAdded[i] : id));
            })
            .Where(l => GmailLabelIds.IsUser(l.Id) && m.LabelIds.Contains(l.Id, StringComparer.Ordinal) && !kept.Contains(l.Name.Trim()));
    }

    /// <summary>The alternative's outcome and provenance; the suggestion keeps its id, message, sender and group.</summary>
    private static void Copy(SuggestionAlternativeRow a, SuggestionRow s)
    {
        s.Source = a.Source;
        s.RunId = a.RunId;
        s.TopicLabel = a.TopicLabel;
        s.IsNewLabel = a.IsNewLabel;
        s.DocumentTypeLabel = a.DocumentTypeLabel;
        s.DocumentTypeIsNew = a.DocumentTypeIsNew;
        s.SetReplaced(a.Replaced(null));
        s.NeedsAction = a.NeedsAction;
        s.ToBeDeleted = a.ToBeDeleted;
        s.UnsubscribeSuggested = a.UnsubscribeSuggested;
        s.Confidence = a.Confidence;
        s.Reason = a.Reason;
        s.FilterCriteria = a.FilterCriteria;
        s.Model = a.Model;
        s.PromptVersion = a.PromptVersion;
        s.Edited = false;
    }
}
