using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>
/// Accepts or discards compare-run alternatives (#249). Accept copies the alternative into its suggestion and makes it
/// pending again, whatever its status: an approval is withdrawn, and an applied suggestion keeps its <c>action_log</c>
/// rows, so Undo of that batch still works on the Gmail state it logged. Nothing changes in Gmail and no decision is
/// recorded; the new outcome is approved and applied the normal way. Endpoints validate first.
/// </summary>
public sealed class AlternativeService(AppDbContext db, TimeProvider time)
{
    /// <summary>Most suggestion ids and groups one request names, each.</summary>
    public const int MaxTargets = 1000;

    /// <summary>
    /// One transaction: locks the suggestions (<c>FOR UPDATE</c>, by id, as the review decisions do; it also waits for a
    /// compare run's checkpoint) and accepts the alternatives still there. A suggestion in an active apply batch is
    /// skipped and counted.
    /// </summary>
    public async Task<AlternativeDecisionResponse> AcceptAsync(Guid[] suggestionIds, GroupRef[] groups, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = await TargetsAsync(suggestionIds, groups, ct);
        var locked = await db.Suggestions.FromSql($"SELECT * FROM suggestions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        var alternatives = await db.SuggestionAlternatives.Where(a => ids.Contains(a.SuggestionId)).ToDictionaryAsync(a => a.SuggestionId, ct);
        var messageIds = locked.ConvertAll(s => s.MessageId);
        var messages = await db.Messages.Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, StringComparer.Ordinal, ct);
        var inApply = await InApplyAsync(ids, ct);
        var now = time.GetUtcNow();
        int accepted = 0, skipped = 0;
        foreach (var suggestion in locked)
        {
            if (!alternatives.TryGetValue(suggestion.Id, out var alternative))
            {
                continue;
            }

            if (inApply(suggestion))
            {
                skipped++;
                continue;
            }

            Copy(alternative, suggestion);
            suggestion.SetStatus(SuggestionStatus.Pending, messages[suggestion.MessageId], now);
            db.SuggestionAlternatives.Remove(alternative);
            accepted++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new AlternativeDecisionResponse(accepted, 0, skipped);
    }

    /// <summary>Deletes the alternatives; the suggestions are not touched.</summary>
    public async Task<AlternativeDecisionResponse> DiscardAsync(Guid[] suggestionIds, GroupRef[] groups, CancellationToken ct)
    {
        var ids = await TargetsAsync(suggestionIds, groups, ct);
        var discarded = await db.SuggestionAlternatives.Where(a => ids.Contains(a.SuggestionId)).ExecuteDeleteAsync(ct);
        return new AlternativeDecisionResponse(0, discarded, 0);
    }

    /// <summary>The named suggestions and the groups' members (any status) that have an alternative, by id.</summary>
    private async Task<Guid[]> TargetsAsync(Guid[] suggestionIds, GroupRef[] groups, CancellationToken ct)
    {
        var ids = new HashSet<Guid>(suggestionIds);
        foreach (var (sender, key) in groups.Select(g => (g.SenderAddress, g.GroupKey)).Distinct())
        {
            ids.UnionWith(await db.Suggestions.AsNoTracking()
                .Where(s => s.SenderAddress == sender && s.GroupKey == key)
                .Select(s => s.Id)
                .ToListAsync(ct));
        }

        var all = ids.ToArray();
        return [.. (await db.SuggestionAlternatives.AsNoTracking()
                .Where(a => all.Contains(a.SuggestionId))
                .Select(a => a.SuggestionId)
                .ToListAsync(ct))
            .Order()];
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

    /// <summary>The alternative's outcome and provenance; the suggestion keeps its id, message, sender and group.</summary>
    private static void Copy(SuggestionAlternativeRow a, SuggestionRow s)
    {
        s.Source = a.Source;
        s.RunId = a.RunId;
        s.TopicLabel = a.TopicLabel;
        s.IsNewLabel = a.IsNewLabel;
        s.DocumentTypeLabel = a.DocumentTypeLabel;
        s.DocumentTypeIsNew = a.DocumentTypeIsNew;
        s.SetReplaced([.. a.ReplaceLabelIds.Select((id, i) => (id, i < a.ReplaceLabels.Length ? a.ReplaceLabels[i] : id))]);
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
