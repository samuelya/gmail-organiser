using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Policies;

/// <param name="PolicyId">The approved policy applied.</param>
/// <param name="After">The last message id walked; the next chunk starts after it.</param>
/// <param name="Matched">Messages that got (or kept) this policy's approved suggestion.</param>
/// <param name="Superseded">Pending suggestions of another source the policy replaced.</param>
/// <param name="Unmatched">Messages no rule matched (a mixed sender's); left for the normal analysis.</param>
/// <param name="Skipped">Messages left alone: deleted in Gmail, already decided, rejected or edited by the user.</param>
/// <param name="Rejected">Of <paramref name="Skipped"/>: a rejected suggestion, which the policy never overrides.</param>
/// <param name="Edited">Of <paramref name="Skipped"/>: this policy's suggestion the user edited in review.</param>
/// <param name="Total">The scope's not-applied messages counted at the start, raised to those seen if more appear.</param>
public sealed record PolicyApplyCursor(
    Guid PolicyId, string? After = null, int Matched = 0, int Superseded = 0, int Unmatched = 0, int Skipped = 0, int Total = 0,
    int Rejected = 0, int Edited = 0)
{
    public int Seen => Matched + Unmatched + Skipped;
}

/// <summary>
/// Applies an approved sender policy to the scope's past mail (#359, DESIGN §6.3) without the LLM. Walks the live,
/// not-applied messages in id order, <see cref="PolicyOptions.ApplyChunkSize"/> at a time; each chunk's suggestions commit with the cursor,
/// and a replayed chunk recomputes the same rows (one suggestion per message), so a restart never duplicates. The final
/// transaction hands every approved suggestion of the policy to the <see cref="ApplyActionsJob"/>, which labels,
/// archives, logs and makes it undoable as any apply, and sets <see cref="SenderPolicyRow.AppliedAt"/> when it completes.
/// </summary>
public sealed class PolicyApplyJob(
    AppDbContext db,
    PolicyMatcher matcher,
    ApplyService apply,
    DecisionRecorder decisions,
    SenderStatsUpdater stats,
    ISettingsStore settingsStore,
    IOptions<PolicyOptions> options,
    TimeProvider time) : IJobHandler
{
    public const string JobType = "policy_apply";
    public const string Queue = JobQueues.Apply;

    public string Type => JobType;

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<PolicyApplyCursor>() ?? throw new JobRefusedException("The policy apply job has no policy.");
        var policy = await db.SenderPolicies.AsNoTracking().Include(p => p.Rules)
            .FirstOrDefaultAsync(p => p.Id == cursor.PolicyId, ct);
        if (policy?.Status != PolicyStatus.Approved)
        {
            throw new JobRefusedException("The policy no longer exists or is not approved.");
        }

        if (PolicyService.Unappliable(policy) is { } reason)
        {
            // Refused before the walk: a policy that would label nothing must not supersede anything either.
            throw new JobRefusedException(reason);
        }

        if (cursor.After is null && cursor.Seen == 0)
        {
            cursor = cursor with { Total = await Walk(policy).CountAsync(ct) };
        }

        var chunkSize = Math.Max(1, options.Value.ApplyChunkSize);
        while (true)
        {
            var chunk = Walk(policy);
            if (cursor.After is { } after)
            {
                chunk = chunk.Where(m => string.Compare(m.Id, after) > 0);
            }

            var ids = await chunk.OrderBy(m => m.Id).Take(chunkSize).Select(m => m.Id).ToListAsync(ct);
            var last = ids.Count < chunkSize;
            while (true)
            {
                var plan = await PlanAsync(policy, ids, ct);
                var next = cursor with
                {
                    After = ids.Count > 0 ? ids[^1] : cursor.After,
                    Matched = cursor.Matched + plan.Count(p => p.Match is not null),
                    Superseded = cursor.Superseded + plan.Count(p => p.Supersedes),
                    Unmatched = cursor.Unmatched + plan.Count(p => p.Match is null && p.Skip is null),
                    Skipped = cursor.Skipped + plan.Count(p => p.Skip is not null),
                    Rejected = cursor.Rejected + plan.Count(p => p.Skip == SkipReason.Rejected),
                    Edited = cursor.Edited + plan.Count(p => p.Skip == SkipReason.Edited),
                };
                next = next with { Total = last ? next.Seen : Math.Max(next.Total, next.Seen) };
                try
                {
                    if (last)
                    {
                        await ctx.CompleteAsync(next, Progress(next), async t =>
                        {
                            await WriteAsync(policy, plan, t);
                            await HandOffAsync(policy, t);
                        }, ct);
                        decisions.Committed();
                        return;
                    }

                    var signal = await ctx.CheckpointAsync(next, Progress(next), t => WriteAsync(policy, plan, t), ct);
                    decisions.Committed();
                    cursor = next;
                    if (signal != JobSignal.Continue)
                    {
                        return;
                    }

                    break;
                }
                catch (PlanChangedException)
                {
                    // Another writer changed one of the chunk's suggestions between the plan and the lock: plan again.
                    db.ChangeTracker.Clear();
                }
            }
        }
    }

    /// <summary>The scope's live messages not yet applied.</summary>
    private IQueryable<MessageRow> Walk(SenderPolicyRow policy) =>
        SenderProfileBuilder.InScope(db, policy.Scope, policy.ScopeKey).Where(m => m.AnalysisStatus != AnalysisStatus.Applied);

    /// <summary>What the chunk does per message, read without locks; <see cref="WriteAsync"/> re-checks it locked.</summary>
    private async Task<List<Planned>> PlanAsync(SenderPolicyRow policy, List<string> ids, CancellationToken ct)
    {
        var messages = await db.Messages.AsNoTracking().Where(m => ids.Contains(m.Id)).OrderBy(m => m.Id).ToListAsync(ct);
        var existing = await db.Suggestions.AsNoTracking().Where(s => ids.Contains(s.MessageId))
            .ToDictionaryAsync(s => s.MessageId, StringComparer.Ordinal, ct);
        return messages.ConvertAll(m => Plan(policy, m, existing.GetValueOrDefault(m.Id)));
    }

    private Planned Plan(SenderPolicyRow policy, MessageRow message, SuggestionRow? existing)
    {
        var state = existing is null ? null : new Existing(existing.Id, existing.Status, existing.PolicyId, existing.Edited);
        var own = IsOwn(policy, existing);

        // Only pending suggestions are superseded. Approved or applied ones of any other source stay (the user, or another
        // policy, already decided), a rejected one stays rejected, and a user edit of this policy's own suggestion wins.
        SkipReason? skip = message.DeletedInGmail ? SkipReason.Deleted
            : existing is { Status: SuggestionStatus.Rejected } ? SkipReason.Rejected
            : own && existing!.Edited ? SkipReason.Edited
            : existing is { Status: SuggestionStatus.Approved or SuggestionStatus.Applied } && !own ? SkipReason.Decided
            : null;
        var match = skip is null ? matcher.Match(message, policy, message.CanonicalAddress) : null;
        return new Planned(message.Id, state, match, skip, match is not null && existing is { Status: SuggestionStatus.Pending });
    }

    private static bool IsOwn(SenderPolicyRow policy, SuggestionRow? existing) =>
        existing?.PolicyId == policy.Id && existing.Status == SuggestionStatus.Approved;

    /// <summary>
    /// Locks the chunk's suggestions, then its messages, by id (the order the review and analysis writers use), checks
    /// the plan still holds and writes it: own suggestions updated in place, superseded ones replaced, a decision for
    /// every new or changed one. An own suggestion that no longer matches (the policy was edited) is removed again.
    /// </summary>
    private async Task WriteAsync(SenderPolicyRow policy, List<Planned> plan, CancellationToken ct)
    {
        var ids = plan.ConvertAll(p => p.MessageId).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var suggestions = (await db.Suggestions
            .FromSql($"SELECT * FROM suggestions WHERE message_id = ANY({ids}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct)).ToDictionary(s => s.MessageId, StringComparer.Ordinal);
        var messages = (await db.Messages
            .FromSql($"SELECT * FROM messages WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct)).ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var p in plan)
        {
            var current = Plan(policy, messages[p.MessageId], suggestions.GetValueOrDefault(p.MessageId));
            if (current.Existing != p.Existing || current.Skip != p.Skip || (current.Match is null) != (p.Match is null))
            {
                throw new PlanChangedException();
            }
        }

        var settings = await settingsStore.GetAsync(ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, [.. messages.Values.Select(m => m.FromAddress).Distinct()], ct);
        var now = time.GetUtcNow();
        var replaced = new List<(MessageRow Message, SuggestionRow Row)>();
        foreach (var p in plan.Where(p => p.Skip is null))
        {
            var message = messages[p.MessageId];
            var existing = suggestions.GetValueOrDefault(p.MessageId);
            var own = IsOwn(policy, existing);
            if (p.Match is not { } match)
            {
                if (own)
                {
                    db.Suggestions.Remove(existing!);
                    message.AnalysisStatus = AnalysisStatus.NotAnalysed;
                    message.UpdatedAt = now;
                }

                continue;
            }

            var row = own ? existing! : new SuggestionRow { Id = Guid.CreateVersion7(now), MessageId = message.Id, CreatedAt = now };
            var before = own ? Outcome(row) : null;
            Fill(row, message, policy, match, MessageProtection.Reason(message, allowlist, settings.Protection));
            if (!own)
            {
                if (existing is not null)
                {
                    // One suggestion per message: the superseded one goes (with its alternatives) before the policy's.
                    db.Suggestions.Remove(existing);
                }

                row.SetStatus(SuggestionStatus.Approved, message, now);
                replaced.Add((message, row));
            }
            else if (!Equals(before, Outcome(row)))
            {
                row.SetStatus(SuggestionStatus.Approved, message, now);
                await decisions.RecordAsync(row, message, DecisionOutcome.Approved, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var (message, row) in replaced)
        {
            db.Suggestions.Add(row);
            await decisions.RecordAsync(row, message, DecisionOutcome.Approved, ct);
        }

        await db.SaveChangesAsync(ct);
        await stats.UpdateAnalysedCountsAsync(messages.Values.Select(m => m.FromAddress), ct);
    }

    /// <summary>
    /// Queues the apply of every approved suggestion of the policy, in the walk's final transaction; with nothing to
    /// apply the policy is applied now.
    /// </summary>
    private async Task HandOffAsync(SenderPolicyRow policy, CancellationToken ct)
    {
        var description = $"Apply policy for {policy.ScopeKey}";
        var batch = await apply.StartAsync(ActionKind.ApplyRest, null, null, description, ct, policy.Id);
        if (batch is null)
        {
            var now = time.GetUtcNow();
            await db.SenderPolicies.Where(p => p.Id == policy.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.AppliedAt, now), ct);
        }
    }

    internal static void Fill(SuggestionRow row, MessageRow message, SenderPolicyRow policy, PolicyMatch match, string? protectedReason)
    {
        var delete = match.Action == PolicyAction.Delete;
        var reason = $"Policy: {match.Rule?.Name ?? "default"}";
        if (match.Guarded)
        {
            reason += "; transactional mail is never deleted";
        }
        else if (delete && protectedReason is not null)
        {
            reason += $"; archived, not deleted (protected: {protectedReason})";
        }

        row.SenderAddress = message.FromAddress;
        row.Source = SuggestionSource.Policy;
        row.TopicLabel = match.TopicLabel;
        row.DocumentTypeLabel = match.DocumentTypeLabel;
        row.MailType = match.MailType;
        row.NeedsAction = match.MailType == MailType.ActionBill;
        row.ToBeDeleted = delete && protectedReason is null;
        row.KeepInInbox = match.Action == PolicyAction.Keep;
        row.UnsubscribeSuggested = match.Action == PolicyAction.Unsubscribe;
        row.Confidence = policy.Confidence;
        row.Reason = reason;
        row.PolicyId = policy.Id;
        row.PolicyRuleId = match.Rule?.Id;
    }

    private static object Outcome(SuggestionRow s) => (s.TopicLabel, s.DocumentTypeLabel, s.MailType, s.NeedsAction, s.ToBeDeleted,
        s.KeepInInbox, s.UnsubscribeSuggested, s.Confidence, s.Reason, s.PolicyRuleId);

    private static JobProgress Progress(PolicyApplyCursor c) => new(
        c.Seen, c.Total, $"Matched {c.Matched}, superseded {c.Superseded}, unmatched {c.Unmatched}, "
            + $"skipped {c.Skipped} (rejected {c.Rejected}, edited {c.Edited})");

    private sealed record Existing(Guid Id, SuggestionStatus Status, Guid? PolicyId, bool Edited);

    private enum SkipReason
    {
        Deleted,
        Decided,
        Rejected,
        Edited,
    }

    /// <param name="Skip">Why the message is left alone, or null when the policy decides it.</param>
    /// <param name="Supersedes">A pending suggestion of another source is replaced.</param>
    private sealed record Planned(string MessageId, Existing? Existing, PolicyMatch? Match, SkipReason? Skip, bool Supersedes);

    private sealed class PlanChangedException : Exception;
}
