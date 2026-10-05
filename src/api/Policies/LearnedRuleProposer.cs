using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Policies.Prompts;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Policies;

/// <summary>
/// Learned sub-rules (#361): an approved suggestion for a message of a mixed sender that no approved rule matches adds
/// a proposed rule (subject template and category, the suggestion's outcome) to the sender's approved policy, so the
/// next message of that template needs no LLM once the user approves the rule. Never approves anything; adds to the
/// caller's unit of work, which saves it with the decision.
/// </summary>
public sealed class LearnedRuleProposer(AppDbContext db, ISettingsStore settingsStore, SenderPolicyOutputParser parser, TimeProvider time)
{
    public const string LearnedReason = "learned from an approved suggestion";

    /// <summary>The covering approved mixed policy per message id, read under its row lock by <see cref="LockAsync"/>; null when nothing is learned.</summary>
    private Dictionary<string, SenderPolicyRow?> covering = new(StringComparer.Ordinal);

    /// <summary>
    /// Locks the approved mixed policies covering the approvals' messages in ascending id order, before any proposal, and
    /// reads them and their rules under the lock. The lock serialises concurrent approvals for a policy, so the same match
    /// is never added twice; the fixed order means two decisions over the same policies never deadlock (#420). The caller
    /// holds the transaction; a later call replaces the policies read by an earlier one.
    /// </summary>
    public async ValueTask LockAsync(IEnumerable<(SuggestionRow Suggestion, MessageRow Message)> approvals, CancellationToken ct)
    {
        var messages = approvals.Where(a => Learns(a.Suggestion)).Select(a => a.Message).DistinctBy(m => m.Id).ToList();
        covering = new Dictionary<string, SenderPolicyRow?>(StringComparer.Ordinal);
        if (messages.Count == 0)
        {
            return;
        }

        var addresses = messages.Select(m => m.CanonicalAddress).Distinct().ToList();
        var listIds = messages.Select(m => GroupKey.NormaliseListId(m.ListId)).OfType<string>().Distinct().ToList();
        var domains = messages.Select(m => m.CanonicalDomain).Distinct().ToList();
        var candidates = await db.SenderPolicies.AsNoTracking()
            .Where(p => p.Status == PolicyStatus.Approved
                && ((p.Scope == PolicyScope.Sender && addresses.Contains(p.ScopeKey))
                    || (p.Scope == PolicyScope.List && listIds.Contains(p.ScopeKey))
                    || (p.Scope == PolicyScope.Domain && domains.Contains(p.ScopeKey))))
            .Select(p => new { p.Id, p.Scope, p.ScopeKey, p.IsMixed })
            .ToListAsync(ct);

        // The most specific policy is the one that covers the message; a non-mixed one leaves nothing to learn.
        var chosen = messages.ToDictionary(m => m.Id, m => candidates
            .Where(p => (p.Scope == PolicyScope.Sender && p.ScopeKey == m.CanonicalAddress)
                || (p.Scope == PolicyScope.List && p.ScopeKey == GroupKey.NormaliseListId(m.ListId))
                || (p.Scope == PolicyScope.Domain && p.ScopeKey == m.CanonicalDomain))
            .OrderBy(p => p.Scope switch { PolicyScope.Sender => 0, PolicyScope.List => 1, _ => 2 })
            .FirstOrDefault() is { IsMixed: true } p ? p.Id : (Guid?)null, StringComparer.Ordinal);
        var ids = chosen.Values.OfType<Guid>().Distinct().Order().ToArray();
        if (ids.Length > 0)
        {
            await db.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM sender_policies WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct);
        }

        // Read again under the lock: a policy rejected or made single-label meanwhile learns nothing, and the stored rules
        // (any status: a rejected one is not proposed again) include any another approval committed meanwhile.
        var locked = await db.SenderPolicies.AsNoTracking().Include(p => p.Rules)
            .Where(p => ids.Contains(p.Id) && p.Status == PolicyStatus.Approved && p.IsMixed)
            .ToDictionaryAsync(p => p.Id, ct);
        foreach (var (messageId, id) in chosen)
        {
            covering[messageId] = id is { } policyId ? locked.GetValueOrDefault(policyId) : null;
        }
    }

    public async ValueTask ProposeAsync(SuggestionRow suggestion, MessageRow message, CancellationToken ct)
    {
        if (!Learns(suggestion))
        {
            return;
        }

        if (!covering.TryGetValue(message.Id, out var policy))
        {
            await LockAsync([(suggestion, message)], ct);
            policy = covering[message.Id];
        }

        // A rule learns from a subject template (#421): without one only the category would remain, and the rule would
        // take every other message of that category the policy's other rules miss.
        var template = SubjectNormaliser.Template(message.Subject);
        if (policy is null || string.IsNullOrWhiteSpace(template)
            || PolicyMatcher.FirstRule(message, policy, message.CanonicalAddress) is not null)
        {
            return;
        }

        var match = new RuleMatch { SubjectTemplate = template, Category = message.Category };

        // Rules added earlier in this unit of work too.
        var rules = policy.Rules.Concat(db.SenderPolicyRules.Local.Where(r => r.PolicyId == policy.Id)).ToList();
        if (rules.Any(r => SameMatch(r.Match, match)))
        {
            return;
        }

        var settings = await settingsStore.GetAsync(ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, [message.FromAddress], ct);
        var rule = parser.CheckLearned(new SenderPolicyRuleRow
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            PolicyId = policy.Id,
            Position = rules.Count == 0 ? 0 : rules.Max(r => r.Position) + 1,
            Match = match,
            TopicLabel = suggestion.TopicLabel,
            DocumentTypeLabel = suggestion.DocumentTypeLabel,
            MailType = suggestion.MailType,
            Action = suggestion.ToBeDeleted ? PolicyAction.Delete : suggestion.NeedsAction ? PolicyAction.Keep : PolicyAction.Archive,
            Status = PolicyStatus.Proposed,
            Source = PolicyRuleSource.Learned,
            Reason = LearnedReason,
            CreatedAt = time.GetUtcNow(),
        }, allowlist.Reason(message.FromAddress) is not null, settings);
        if (rule is not null)
        {
            db.SenderPolicyRules.Add(rule);
        }
    }

    private static bool Learns(SuggestionRow suggestion) =>
        suggestion.Source is (SuggestionSource.Llm or SuggestionSource.Derived or SuggestionSource.Memory or SuggestionSource.Stage0)
        && !string.IsNullOrWhiteSpace(suggestion.TopicLabel);

    private static bool SameMatch(RuleMatch a, RuleMatch b) =>
        a.ListIdPresent == b.ListIdPresent && a.ListUnsubscribePresent == b.ListUnsubscribePresent && a.Category == b.Category
        && Same(a.FromAddress, b.FromAddress) && Same(a.FromSubdomain, b.FromSubdomain)
        && Same(a.SubjectTemplate, b.SubjectTemplate) && Same(a.SubjectContains, b.SubjectContains);

    private static bool Same(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.OrdinalIgnoreCase);
}
