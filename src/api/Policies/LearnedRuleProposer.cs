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

    public async ValueTask ProposeAsync(SuggestionRow suggestion, MessageRow message, CancellationToken ct)
    {
        if (suggestion.Source is not (SuggestionSource.Llm or SuggestionSource.Derived or SuggestionSource.Memory or SuggestionSource.Stage0)
            || string.IsNullOrWhiteSpace(suggestion.TopicLabel))
        {
            return;
        }

        var listId = GroupKey.NormaliseListId(message.ListId);
        var candidates = await db.SenderPolicies.AsNoTracking().Include(p => p.Rules)
            .Where(p => p.Status == PolicyStatus.Approved
                && ((p.Scope == PolicyScope.Sender && p.ScopeKey == message.CanonicalAddress)
                    || (p.Scope == PolicyScope.List && p.ScopeKey == listId)
                    || (p.Scope == PolicyScope.Domain && p.ScopeKey == message.CanonicalDomain)))
            .ToListAsync(ct);

        // The most specific policy is the one that covers the message; a non-mixed one leaves nothing to learn.
        var policy = candidates.OrderBy(p => p.Scope switch { PolicyScope.Sender => 0, PolicyScope.List => 1, _ => 2 }).FirstOrDefault();
        if (policy is not { IsMixed: true } || PolicyMatcher.FirstRule(message, policy, message.CanonicalAddress) is not null)
        {
            return;
        }

        var match = new RuleMatch
        {
            SubjectTemplate = SubjectNormaliser.Template(message.Subject) is { Length: > 0 } template ? template : null,
            Category = message.Category,
        };

        // Stored rules of any status (a rejected one is not proposed again) and rules added earlier in this unit of work.
        var rules = policy.Rules.Concat(db.SenderPolicyRules.Local.Where(r => r.PolicyId == policy.Id)).ToList();
        if (match.IsEmpty || rules.Any(r => SameMatch(r.Match, match)))
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

    private static bool SameMatch(RuleMatch a, RuleMatch b) =>
        a.ListIdPresent == b.ListIdPresent && a.ListUnsubscribePresent == b.ListUnsubscribePresent && a.Category == b.Category
        && Same(a.FromAddress, b.FromAddress) && Same(a.FromSubdomain, b.FromSubdomain)
        && Same(a.SubjectTemplate, b.SubjectTemplate) && Same(a.SubjectContains, b.SubjectContains);

    private static bool Same(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.OrdinalIgnoreCase);
}
