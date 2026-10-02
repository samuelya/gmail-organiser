using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Senders;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GmailOrganiser.Review;

public enum ApplyRestStatus
{
    Ok,

    /// <summary>No approved pattern and no topic label given, or the pattern's label is one Gmail would refuse.</summary>
    NoPattern,

    /// <summary>Another writer (an analysis run) created a suggestion for one of the messages meanwhile.</summary>
    Conflict,
}

/// <summary>
/// "Apply to rest of sender" (DESIGN §6.3): the outcome the user approved most for a sender, applied to the sender's
/// remaining messages without the LLM through the <see cref="ApplyActionsJob"/>. Pattern suggestions are excluded from
/// the pattern itself, so it only ever reflects the user's own approvals.
/// </summary>
public sealed class SenderPatternService(
    AppDbContext db, ApplyService apply, SenderStatsUpdater stats, TimeProvider time)
{
    public const string Reason = "Applied the approved pattern of this sender";

    public async Task<SenderPatternDto> GetAsync(string address, CancellationToken ct)
    {
        var outcomes = await OutcomesAsync(address, ct);
        var approvals = outcomes.Sum(o => o.Count);
        var top = outcomes.FirstOrDefault();
        return new SenderPatternDto(
            top?.TopicLabel,
            top?.NeedsAction,
            top?.ToBeDeleted,
            approvals,
            top is null ? 0 : (double)top.Count / approvals,
            await Remaining(address).CountAsync(ct));
    }

    /// <summary>
    /// Creates an approved suggestion for every remaining message of <paramref name="address"/> (each request value
    /// overrides the pattern's), one decision and the apply batch, in one transaction.
    /// </summary>
    public async Task<(ApplyRestStatus Status, ApplyRestResponse? Response)> ApplyRestAsync(
        string address, ApplyRestRequest request, CancellationToken ct)
    {
        var outcomes = await OutcomesAsync(address, ct);
        var top = outcomes.FirstOrDefault();
        var label = request.TopicLabel?.Trim() ?? top?.TopicLabel;
        if (label is null || !LabelResolver.IsValid(label))
        {
            return (ApplyRestStatus.NoPattern, null);
        }

        var needsAction = request.NeedsAction ?? top?.NeedsAction ?? false;
        var toBeDeleted = request.ToBeDeleted ?? top?.ToBeDeleted ?? false;
        var approvals = outcomes.Sum(o => o.Count);
        var agreeing = outcomes
            .Where(o => o.TopicLabel == label && o.NeedsAction == needsAction && o.ToBeDeleted == toBeDeleted)
            .Sum(o => o.Count);
        var agreement = approvals == 0 ? 0 : (double)agreeing / approvals;
        var edited = top is not null && (top.TopicLabel != label || top.NeedsAction != needsAction || top.ToBeDeleted != toBeDeleted);
        var filter = new FilterCandidateDto(address, await CommonListIdAsync(address, ct));

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Suggestions, then messages, each by id: the order the review endpoints and the analysis store lock in. A
        // group being stored finishes first and its suggestions are then seen below; a run storing later waits.
        await db.Suggestions
            .FromSql($"SELECT * FROM suggestions WHERE sender_address = {address} ORDER BY id FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(ct);
        await db.Database
            .SqlQuery<string>($"SELECT id AS \"Value\" FROM messages WHERE from_address = {address} ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        var messages = await Remaining(address).OrderBy(m => m.Id).ToListAsync(ct);
        if (messages.Count == 0)
        {
            return (ApplyRestStatus.Ok, new ApplyRestResponse(0, 0, null, filter));
        }

        var allowlisted = await db.Senders.AnyAsync(s => s.Address == address && s.Allowlisted, ct);
        var now = time.GetUtcNow();
        var ids = new Guid[messages.Count];
        var protectedAdjusted = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var isProtected = MessageProtection.IsProtected(message, allowlisted);
            protectedAdjusted += toBeDeleted && isProtected ? 1 : 0;
            var suggestion = new SuggestionRow
            {
                Id = ids[i] = Guid.CreateVersion7(now),
                MessageId = message.Id,
                SenderAddress = address,
                Source = SuggestionSource.SenderPattern,
                TopicLabel = label,
                NeedsAction = needsAction,
                ToBeDeleted = toBeDeleted && !isProtected,
                Confidence = agreement,
                Reason = Reason,
                Edited = edited,
                CreatedAt = now,
            };
            suggestion.SetStatus(SuggestionStatus.Approved, message, now);
            db.Suggestions.Add(suggestion);
        }

        // One decision for the whole action, tied to no message. ScopeKey stays null: it spans the sender's templates,
        // and memory patterns never count sender-pattern approvals anyway, so apply-rest cannot reinforce itself.
        db.Decisions.Add(new DecisionRow
        {
            Id = Guid.CreateVersion7(now),
            SenderAddress = address,
            ListId = filter.ListId,
            TopicLabel = label,
            NeedsAction = needsAction,
            ToBeDeleted = toBeDeleted,
            Outcome = DecisionOutcome.Approved,
            Source = SuggestionSource.SenderPattern,
            Edited = edited,
            CreatedAt = now,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsSuggestionConflict(ex))
        {
            return (ApplyRestStatus.Conflict, null);
        }

        await stats.UpdateAnalysedCountsAsync([address], ct);
        var description = $"Apply rest of {address}: {messages.Count} message{(messages.Count == 1 ? "" : "s")}";
        var batch = await apply.StartAsync(ActionKind.ApplyRest, address, ids, description, ct);
        await tx.CommitAsync(ct);
        return (ApplyRestStatus.Ok, new ApplyRestResponse(messages.Count, protectedAdjusted, batch, filter));
    }

    /// <summary>The sender's messages nobody has suggested anything for yet and that still exist in Gmail.</summary>
    private IQueryable<MessageRow> Remaining(string address) =>
        db.Messages.Where(m => m.FromAddress == address
            && !m.DeletedInGmail
            && m.AnalysisStatus == AnalysisStatus.NotAnalysed
            && !db.Suggestions.Any(s => s.MessageId == m.Id));

    /// <summary>The user's approved outcomes for the sender, most common first, ties to the latest decision.</summary>
    private async Task<List<Outcome>> OutcomesAsync(string address, CancellationToken ct)
    {
        var rows = await db.Suggestions
            .Where(s => s.SenderAddress == address
                && s.Source != SuggestionSource.SenderPattern
                && (s.Status == SuggestionStatus.Approved || s.Status == SuggestionStatus.Applied))
            .GroupBy(s => new { s.TopicLabel, s.NeedsAction, s.ToBeDeleted })
            .Select(g => new { g.Key.TopicLabel, g.Key.NeedsAction, g.Key.ToBeDeleted, Count = g.Count(), Last = g.Max(s => s.DecidedAt) })
            .ToListAsync(ct);
        return [.. rows
            .OrderByDescending(o => o.Count)
            .ThenByDescending(o => o.Last)
            .ThenBy(o => o.TopicLabel, StringComparer.Ordinal)
            .Select(o => new Outcome(o.TopicLabel, o.NeedsAction, o.ToBeDeleted, o.Count))];
    }

    /// <summary>The List-Id every stored message of the sender carries, or null when they differ or some have none.</summary>
    private async Task<string?> CommonListIdAsync(string address, CancellationToken ct)
    {
        var listIds = await db.Messages
            .Where(m => m.FromAddress == address && !m.DeletedInGmail)
            .Select(m => m.ListId)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);
        return listIds is [{ } only] ? only : null;
    }

    private static bool IsSuggestionConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "suggestions" };

    private sealed record Outcome(string TopicLabel, bool NeedsAction, bool ToBeDeleted, int Count);
}
