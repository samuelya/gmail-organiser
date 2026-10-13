using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Review;

/// <summary>What <see cref="AlternativeCopyService.CopyAsync"/> did.</summary>
public enum AlternativeCopyResult
{
    Ok,
    NotFound,
    AnalysisActive,
}

/// <summary>
/// Copies one suggestion's current outcome to similar pending suggestions as alternatives (#562), without the LLM: each copy
/// still goes through accept or discard (<see cref="AlternativeService"/>) and then the normal approve and apply. Nothing
/// changes in Gmail, no decision is recorded and the suggestions are not touched. One transaction per request (the endpoint
/// caps the ids at <see cref="SettingsValidation.MaxAnalysisDefaultCount"/>): targets are locked <c>FOR UPDATE</c> and the source <c>FOR SHARE</c>
/// in id order, so the copy waits for (and never deadlocks with) accept, discard and a compare run's checkpoint, and every
/// target gets the same outcome. Endpoints validate first.
/// </summary>
public sealed class AlternativeCopyService(AppDbContext db, TimeProvider time)
{
    /// <summary>
    /// Writes a copy of the source's outcome for every target that is, under lock, pending, from the source's sender, not
    /// the source and whose message is not deleted in Gmail; it replaces the target's alternative. Refused while an analysis
    /// run is queued or running, checked again under the locks. Run start takes no lock, so a run enqueued but not yet
    /// committed is not seen; a compare run then replaces the copies (later-created wins).
    /// </summary>
    public async Task<(AlternativeCopyResult Result, AlternativeCopyResponse? Response)> CopyAsync(
        Guid sourceId, Guid[] suggestionIds, CancellationToken ct)
    {
        if (!await db.Suggestions.AnyAsync(s => s.Id == sourceId, ct))
        {
            return (AlternativeCopyResult.NotFound, null);
        }

        if (await AnalysisActiveAsync(ct))
        {
            return (AlternativeCopyResult.AnalysisActive, null);
        }

        var targets = suggestionIds.Distinct().ToArray();
        var ids = targets.Where(id => id != sourceId).Order().ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Rows lock in id order across the three queries (Postgres compares the ids), like every other locker of suggestions.
        var locked = await db.Suggestions.FromSql(
                $"SELECT * FROM suggestions WHERE id = ANY({ids}) AND id < {sourceId} ORDER BY id FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var source = (await db.Suggestions.FromSql($"SELECT * FROM suggestions WHERE id = {sourceId} FOR SHARE")
            .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        locked.AddRange(await db.Suggestions.FromSql(
                $"SELECT * FROM suggestions WHERE id = ANY({ids}) AND id > {sourceId} ORDER BY id FOR UPDATE")
            .AsNoTracking().ToListAsync(ct));
        if (source is null)
        {
            return (AlternativeCopyResult.NotFound, null);
        }

        if (await AnalysisActiveAsync(ct))
        {
            return (AlternativeCopyResult.AnalysisActive, null);
        }

        var valid = locked.Where(s => s.Status == SuggestionStatus.Pending && s.SenderAddress == source.SenderAddress).ToList();
        var messageIds = valid.ConvertAll(s => s.MessageId);
        var labels = await db.Messages.AsNoTracking()
            .Where(m => messageIds.Contains(m.Id) && !m.DeletedInGmail)
            .Select(m => new { m.Id, m.LabelIds })
            .ToDictionaryAsync(m => m.Id, m => m.LabelIds, StringComparer.Ordinal, ct);
        valid.RemoveAll(s => !labels.ContainsKey(s.MessageId));
        var validIds = valid.ConvertAll(s => s.Id);
        await db.SuggestionAlternatives.Where(a => validIds.Contains(a.SuggestionId)).ExecuteDeleteAsync(ct);
        var now = time.GetUtcNow();
        db.SuggestionAlternatives.AddRange(valid.Select(s => SuggestionAlternativeRow.CopyOf(source, s, labels[s.MessageId], now)));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return (AlternativeCopyResult.Ok, new AlternativeCopyResponse(valid.Count, targets.Length - valid.Count));
    }

    private Task<bool> AnalysisActiveAsync(CancellationToken ct) =>
        db.AnalysisRuns.AnyAsync(r => r.Status == AnalysisRunStatus.Queued || r.Status == AnalysisRunStatus.Running, ct);
}
