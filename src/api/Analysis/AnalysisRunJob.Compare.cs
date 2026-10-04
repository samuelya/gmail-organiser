using GmailOrganiser.Jobs;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Analysis;

/// <summary>
/// The compare-run half of <see cref="AnalysisRunJob"/> (#248): the run's rows become alternatives of the suggestions
/// frozen at its start; suggestions, messages and sender counts are never written.
/// </summary>
public sealed partial class AnalysisRunJob
{
    /// <summary>The candidates whose frozen suggestion still exists (a normal re-analyse may have replaced it).</summary>
    private async Task<HashSet<string>> CurrentSuggestionsAsync(AnalysisRunCursor cursor, CancellationToken ct)
    {
        var frozen = cursor.SuggestionIds ?? throw new JobRefusedException("The compare run has no frozen suggestions.");
        var ids = frozen.Values.ToArray();
        var present = await db.Suggestions.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.MessageId })
            .ToListAsync(ct);
        return present
            .Where(s => frozen.TryGetValue(s.MessageId, out var id) && id == s.Id)
            .Select(s => s.MessageId)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Replaces the alternatives of the group's suggestions with this run's; a suggestion deleted meanwhile, or one whose
    /// alternative comes from a compare run created later (a resumed older run finishing after a newer one), counts as
    /// skipped. Delete-then-insert by suggestion keeps a repeated group (restart before its commit) from writing twice.
    /// </summary>
    private async Task WriteAlternativesAsync(
        AnalysisRunRow run, IReadOnlyDictionary<string, Guid> suggestionIds, List<SuggestionRow> rows, DateTimeOffset now, CancellationToken c)
    {
        // FOR KEY SHARE holds off a concurrent delete until commit; a suggestion deleted before it is not returned.
        var wanted = rows.Where(r => suggestionIds.ContainsKey(r.MessageId)).Select(r => suggestionIds[r.MessageId]).ToArray();
        var present = (await db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM suggestions WHERE id = ANY({wanted}) ORDER BY id FOR KEY SHARE")
                .ToListAsync(c))
            .ToHashSet();
        // Runs freeze their suggestions when created and keep created_at on resume, so the later-created run wins.
        var locked = present.ToArray();
        var newer = await db.SuggestionAlternatives
            .Where(a => locked.Contains(a.SuggestionId)
                && db.AnalysisRuns.Any(r => r.Id == a.RunId && r.Id != run.Id && r.CreatedAt > run.CreatedAt))
            .Select(a => a.SuggestionId)
            .ToListAsync(c);
        present.ExceptWith(newer);

        foreach (var gone in rows.Where(r => !suggestionIds.TryGetValue(r.MessageId, out var id) || !present.Contains(id)))
        {
            run.MessagesCovered--;
            run.MessagesLlm -= gone.Source == SuggestionSource.Llm ? 1 : 0;
            run.MessagesDerived -= gone.Source == SuggestionSource.Derived ? 1 : 0;
            run.SkippedMessages++;
        }

        var kept = present.ToArray();
        await db.SuggestionAlternatives.Where(a => kept.Contains(a.SuggestionId)).ExecuteDeleteAsync(c);
        foreach (var row in rows)
        {
            if (suggestionIds.TryGetValue(row.MessageId, out var id) && present.Contains(id))
            {
                db.SuggestionAlternatives.Add(SuggestionAlternativeRow.From(row, id, run.Id, now));
            }
        }

        await db.SaveChangesAsync(c);
    }
}
