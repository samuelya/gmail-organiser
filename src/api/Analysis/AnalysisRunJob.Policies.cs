using GmailOrganiser.Data;
using GmailOrganiser.Jobs;
using GmailOrganiser.Llm;
using GmailOrganiser.Policies;
using GmailOrganiser.Policies.Prompts;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Npgsql;

namespace GmailOrganiser.Analysis;

/// <summary>
/// The top senders half of <see cref="AnalysisRunJob"/> (#357, DESIGN §6.2): one policy call per frozen sender, the
/// proposed policy stored with the run counters and the cursor in one transaction. A restart before that commit redoes
/// only that sender; one that has a proposed or approved policy by then is skipped, so a policy is never written twice.
/// No grouping, memory short-circuit or triage model.
/// </summary>
public sealed partial class AnalysisRunJob
{
    private async Task RunPoliciesAsync(JobContext ctx, AnalysisRunRow run, AnalysisRunCursor cursor, CancellationToken ct)
    {
        var chatConfig = await llm.EnsureChatConfiguredAsync(ct);
        var (settings, model) = (chatConfig.Settings, chatConfig.Model);
        var senders = cursor.Senders ?? throw new JobRefusedException("The top senders run has no frozen senders.");

        run.Status = AnalysisRunStatus.Running;
        run.StartedAt ??= time.GetUtcNow();
        run.FinishedAt = null;
        run.Error = null;
        run.Model = model;
        run.PromptVersion = SenderPolicyPromptBuilder.Version;
        await db.SaveChangesAsync(ct);

        var (labelTree, _) = await UserLabelsAsync(settings, ct);
        var labelIndex = new LabelTreeIndex(labelTree);
        using var client = llm.CreateChatClient(chatConfig);
        var chat = ActiveChat(client, chatConfig);
        while (cursor.NextSenderIndex < senders.Count)
        {
            var sender = senders[cursor.NextSenderIndex];
            var outcome = await ProposeAsync(run, sender, settings, labelTree, labelIndex, chat, ct);
            cursor = cursor with { NextSenderIndex = cursor.NextSenderIndex + 1, LastScopeKey = sender.ScopeKey };
            var signal = await StorePolicyAsync(ctx, run, sender, outcome, cursor, ct);
            if (signal == JobSignal.Cancel)
            {
                await FinishRunAsync(run.Id, AnalysisRunStatus.Cancelled, null, ct);
                return;
            }

            if (signal == JobSignal.Pause)
            {
                return;
            }
        }

        await ctx.CompleteAsync(cursor, PolicyProgress(run, cursor), async c =>
        {
            run.Status = AnalysisRunStatus.Completed;
            run.FinishedAt = time.GetUtcNow();
            await db.SaveChangesAsync(c);
        }, ct);
    }

    /// <summary>What one sender's step produced: a proposal, an invalid answer, or nothing (skipped, no call).</summary>
    private sealed record PolicyOutcome(SenderPolicyRow? Policy, bool Failed, int LlmCalls, LlmUsage Usage)
    {
        public static readonly PolicyOutcome Skipped = new(null, false, 0, default);
    }

    /// <summary>
    /// The sender's profile (with bodies), the policy prompt, one chat call and the parsed proposal. A sender that got a
    /// proposed or approved policy since the run started (or a rejected one, when the run walks all senders), or has no
    /// live mail left, is skipped without a call.
    /// </summary>
    private async Task<PolicyOutcome> ProposeAsync(
        AnalysisRunRow run, PolicyCandidate sender, AppSettings settings, IReadOnlyList<string> labelTree, LabelTreeIndex labelIndex,
        MeteredChat chat, CancellationToken ct)
    {
        if (await HasTakenPolicyAsync(run, sender, ct)
            || await profiles.BuildAsync(sender.Scope, sender.ScopeKey, includeBodies: true, ct) is not { } profile)
        {
            return PolicyOutcome.Skipped;
        }

        var prompt = SenderPolicyPromptBuilder.Build(profile, labelTree, settings);
        var (text, usage) = await ChatAsync(
            chat, run.Model, prompt.Messages, SenderPolicyPromptBuilder.CreateOptions(settings.LlmNumCtx), 1, ct);
        var parsed = policyParser.Parse(text, profile, labelIndex, settings);
        if (parsed.Dropped.Count > 0)
        {
            LogDroppedOutput(logger, string.Join("; ", parsed.Dropped));
        }

        if (parsed.Policy is not { } policy)
        {
            LogInvalidPolicy(logger, string.Join("; ", parsed.Errors));
            return new PolicyOutcome(null, true, 1, usage);
        }

        var now = time.GetUtcNow();
        policy.Id = Guid.CreateVersion7(now);
        policy.RunId = run.Id;
        policy.Model = run.Model;
        policy.CreatedAt = now;
        for (var i = 0; i < policy.Rules.Count; i++)
        {
            policy.Rules[i].Id = Guid.CreateVersion7(now);
            policy.Rules[i].PolicyId = policy.Id;
            policy.Rules[i].Position = i;
            policy.Rules[i].CreatedAt = now;
        }

        return new PolicyOutcome(policy, false, 1, usage);
    }

    private Task<bool> HasTakenPolicyAsync(AnalysisRunRow run, PolicyCandidate sender, CancellationToken ct)
    {
        var walk = run.SenderAddress is null;
        return db.SenderPolicies.AsNoTracking().AnyAsync(
            p => p.Scope == sender.Scope && p.ScopeKey == sender.ScopeKey
                && (p.Status == PolicyStatus.Proposed || p.Status == PolicyStatus.Approved
                    || (walk && p.Status == PolicyStatus.Rejected)),
            ct);
    }

    /// <summary>Proposed and approved policies are always taken; a rejected one only when the run walks all senders.</summary>
    private static bool IsTaken(AnalysisRunRow run, PolicyStatus status) =>
        status is PolicyStatus.Proposed or PolicyStatus.Approved || (status == PolicyStatus.Rejected && run.SenderAddress is null);

    /// <summary>
    /// One transaction: the proposed policy and its rules (a single-sender run replaces a rejected policy of the same
    /// scope key), the run counters and the checkpoint. A unique violation means another writer stored a policy for the key after the check:
    /// the retry starts from the stored counters and its check skips the sender. Returns the pause/cancel signal.
    /// </summary>
    private async Task<JobSignal> StorePolicyAsync(
        JobContext ctx, AnalysisRunRow run, PolicyCandidate sender, PolicyOutcome outcome, AnalysisRunCursor cursor, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            Count(run, sender, outcome);
            try
            {
                return await ctx.CheckpointAsync(cursor, PolicyProgress(run, cursor), c => WritePolicyAsync(run, sender, outcome, c), ct);
            }
            catch (DbUpdateException ex) when (attempt < MaxStoreAttempts && IsPolicyConflict(ex))
            {
                db.ChangeTracker.Clear();
                db.AnalysisRuns.Attach(run);
                await db.Entry(run).ReloadAsync(ct);
            }
        }
    }

    private static void Count(AnalysisRunRow run, PolicyCandidate sender, PolicyOutcome outcome)
    {
        run.LlmCalls += outcome.LlmCalls;
        run.Groups += outcome.LlmCalls;
        run.FailedMessages += outcome.Failed ? 1 : 0;
        run.PromptTokens += outcome.Usage.PromptTokens;
        run.CompletionTokens += outcome.Usage.CompletionTokens;
        run.LlmMilliseconds += outcome.Usage.Milliseconds;
        run.NearContextLimit += outcome.Usage.NearContextLimit;
        if (outcome.Policy is { } policy)
        {
            run.PoliciesProposed++;
            run.MixedGroups += policy.IsMixed ? 1 : 0;
            run.MessagesCovered += sender.Messages;
        }
        else if (!outcome.Failed)
        {
            run.SkippedMessages += sender.Messages;
        }
    }

    /// <summary>Under the key's row lock: skips a sender whose key got taken meanwhile, otherwise replaces a rejected policy.</summary>
    private async Task WritePolicyAsync(AnalysisRunRow run, PolicyCandidate sender, PolicyOutcome outcome, CancellationToken c)
    {
        if (outcome.Policy is { } policy)
        {
            var scope = SnakeCaseEnumConverter<PolicyScope>.ToDb(sender.Scope);
            var existing = await db.Database
                .SqlQuery<string>(
                    $"SELECT status AS \"Value\" FROM sender_policies WHERE scope = {scope} AND scope_key = {sender.ScopeKey} FOR UPDATE")
                .ToListAsync(c);
            if (existing.Any(s => IsTaken(run, SnakeCaseEnumConverter<PolicyStatus>.FromDb(s))))
            {
                run.PoliciesProposed--;
                run.MixedGroups -= policy.IsMixed ? 1 : 0;
                run.MessagesCovered -= sender.Messages;
                run.SkippedMessages += sender.Messages;
            }
            else
            {
                await db.SenderPolicies
                    .Where(p => p.Scope == sender.Scope && p.ScopeKey == sender.ScopeKey && p.Status == PolicyStatus.Rejected)
                    .ExecuteDeleteAsync(c);
                db.SenderPolicies.Add(policy);
            }
        }

        await db.SaveChangesAsync(c);
    }

    private static bool IsPolicyConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "sender_policies" };

    /// <summary>Senders stored (proposed, failed or skipped) out of the frozen ones.</summary>
    private static JobProgress PolicyProgress(AnalysisRunRow run, AnalysisRunCursor cursor) =>
        new(cursor.NextSenderIndex, cursor.Senders?.Count, $"{run.PoliciesProposed} policies proposed, {run.LlmCalls} LLM calls");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sender policy output invalid; the sender counts as failed: {Errors}")]
    private static partial void LogInvalidPolicy(ILogger logger, string errors);
}
