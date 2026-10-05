using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GmailOrganiser.Senders;

/// <summary>The result of a Stage-0 action, or why it was refused (422) or lost a race (409).</summary>
public sealed record Stage0Result<T>(T? Value, string? Refusal = null, bool Conflict = false)
    where T : class;

/// <summary>
/// Rule-based actions for noisy senders, no LLM (#349, DESIGN §6.4): pending suggestions through Review, and the sender
/// archive job. Neither ever targets a human, replied-to or allowlisted sender; protected mail is skipped and counted.
/// </summary>
public sealed class Stage0Service(
    AppDbContext db, ISettingsStore settingsStore, LabelCatalog catalog, SenderStatsUpdater stats, IJobService jobs, TimeProvider time)
{
    public const string PromptVersion = "stage0-v1";

    /// <summary>Stage-0 suggestions of one canonical sender share a review group: <c>stage0:</c> plus the canonical address.</summary>
    public const string GroupKeyPrefix = "stage0:";

    /// <summary>
    /// One pending <see cref="SuggestionSource.Stage0"/> suggestion per live, unprotected, not yet suggested message of the
    /// senders that is not delete-labelled; nothing when a sender is refused.
    /// </summary>
    /// <exception cref="Gmail.GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<Stage0Result<Stage0ProposalsResponse>> ProposeAsync(
        string[] canonical, bool toBeDeleted, bool unsubscribe, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var senders = await NoisySenderQuery.Stage0SendersAsync(db, settings.Protection.AllowlistedDomains, canonical, ct);
        if (Refusal(canonical, senders) is { } refusal)
        {
            return new(null, refusal);
        }

        var deleteLabel = await catalog.FindByNameAsync(settings.DeleteLabelName, ct);
        var raw = await db.Senders.Where(s => canonical.Contains(s.CanonicalAddress)).Select(s => s.Address).ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Suggestions, then messages, each by id: the order the review endpoints and the analysis store lock in.
        await db.Suggestions
            .FromSql($"SELECT * FROM suggestions WHERE sender_address = ANY({raw}) ORDER BY id FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(ct);
        var messages = (await db.Messages
                .FromSql($"SELECT * FROM messages WHERE canonical_address = ANY({canonical}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct))
            .Where(m => !m.DeletedInGmail && (deleteLabel is null || !m.LabelIds.Contains(deleteLabel.Id, StringComparer.Ordinal)))
            .ToList();
        string[] ids = [.. messages.Select(m => m.Id)];
        var suggested = (await db.Suggestions.Where(s => ids.Contains(s.MessageId)).Select(s => s.MessageId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, [.. messages.Select(m => m.FromAddress).Distinct()], ct);

        var now = time.GetUtcNow();
        var (created, skippedProtected, skippedSuggested) = (new List<string>(), 0, 0);
        foreach (var message in messages)
        {
            if (message.AnalysisStatus != AnalysisStatus.NotAnalysed || suggested.Contains(message.Id))
            {
                skippedSuggested++;
                continue;
            }

            if (MessageProtection.Reason(message, allowlist, settings.Protection) is not null)
            {
                skippedProtected++;
                continue;
            }

            var sender = senders[message.CanonicalAddress];
            var suggestion = new SuggestionRow
            {
                Id = Guid.CreateVersion7(now),
                MessageId = message.Id,
                SenderAddress = message.FromAddress,
                GroupKey = GroupKeyPrefix + message.CanonicalAddress,
                Source = SuggestionSource.Stage0,
                TopicLabel = settings.DeleteLabelName,
                IsNewLabel = deleteLabel is null,
                ToBeDeleted = toBeDeleted,
                UnsubscribeSuggested = unsubscribe,
                Confidence = 1.0,
                Reason = Reason(sender),
                PromptVersion = PromptVersion,
                CreatedAt = now,
            };
            suggestion.SetStatus(SuggestionStatus.Pending, message, now);
            db.Suggestions.Add(suggestion);
            created.Add(message.FromAddress);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "suggestions" })
        {
            return new(null, Conflict: true);
        }

        await stats.UpdateAnalysedCountsAsync(created, ct);
        await tx.CommitAsync(ct);
        return new(new Stage0ProposalsResponse(created.Count, skippedProtected, skippedSuggested));
    }

    /// <summary>
    /// Records the <see cref="ActionKind.Archive"/> batch and queues its <see cref="SenderArchiveJob"/> in one transaction;
    /// refused when a sender is or when no message qualifies. No Gmail writes.
    /// </summary>
    /// <exception cref="Gmail.GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<Stage0Result<JobDto>> StartArchiveAsync(string[] canonical, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        var senders = await NoisySenderQuery.Stage0SendersAsync(db, settings.Protection.AllowlistedDomains, canonical, ct);
        if (Refusal(canonical, senders) is { } refusal)
        {
            return new(null, refusal);
        }

        var deleteLabelId = (await catalog.FindByNameAsync(settings.DeleteLabelName, ct))?.Id;
        var (covered, _) = await SenderArchiveJob.PlanAsync(db, canonical, deleteLabelId, settings, ct);
        if (covered.Count == 0)
        {
            return new(null, "Nothing to archive: these senders have no unprotected mail in the inbox.");
        }

        var now = time.GetUtcNow();
        var batch = new ActionBatchRow
        {
            Id = Guid.CreateVersion7(now),
            Kind = ActionKind.Archive,
            Description = SenderArchiveJob.Describe(covered.Count, canonical.Length),
            CreatedAt = now,
        };

        // One transaction, so the job never runs without its batch and the batch always names its job.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.ActionBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        var (job, _) = await jobs.EnqueueAsync(
            SenderArchiveJob.JobType, SenderArchiveJob.Queue, new SenderArchiveCursor(batch.Id, canonical, deleteLabelId), ct, batch.Id.ToString());
        batch.JobId = job.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(job);
    }

    /// <summary>Numbers only: the sender's totals as the noisy list shows them.</summary>
    public static string Reason(Stage0Sender sender) =>
        $"Stage 0: {sender.TotalCount} messages, "
        + $"{(sender.TotalCount == 0 ? 0 : (int)Math.Round(100.0 * sender.UnreadCount / sender.TotalCount))}% unread, "
        + "never replied, bulk headers";

    /// <summary>Why the first refused sender may not be targeted, naming it; null when every one may.</summary>
    private static string? Refusal(string[] canonical, Dictionary<string, Stage0Sender> senders)
    {
        foreach (var address in canonical)
        {
            if (!senders.TryGetValue(address, out var sender))
            {
                return $"{address} is not a known sender.";
            }

            if (sender.Excluded)
            {
                return $"{address} is a human, replied-to or allowlisted sender; Stage 0 never targets it.";
            }
        }

        return null;
    }
}
