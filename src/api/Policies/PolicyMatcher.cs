using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;

namespace GmailOrganiser.Policies;

/// <summary>
/// A policy's outcome for one message. <see cref="Rule"/> is the matched sub-rule, null for the policy default;
/// <see cref="Guarded"/> means the transactional guard turned a delete into an archive.
/// </summary>
public sealed record PolicyMatch(
    SenderPolicyRuleRow? Rule,
    string TopicLabel,
    string? DocumentTypeLabel,
    MailType? MailType,
    int? RetentionDays,
    PolicyAction Action,
    bool Guarded);

/// <summary>
/// Decides what a sender policy does to one message, without the LLM (DESIGN §6.2). Pure: reads only the message, the
/// policy and its loaded <see cref="SenderPolicyRow.Rules"/>. The caller picks the policy and checks its status.
/// </summary>
public sealed class PolicyMatcher(TransactionalGuard guard)
{
    /// <summary>
    /// <list type="number">
    /// <item>The first approved rule, in <see cref="CostOrder"/>, whose every set match field holds.</item>
    /// <item>When the message is transactional and no rule matched or the rule deletes: a mixed policy gives null (review);
    /// otherwise the policy default with delete downgraded to archive, <see cref="PolicyMatch.Guarded"/> set.</item>
    /// <item>A matched rule gives its outcome.</item>
    /// <item>No rule: the default of a non-mixed policy, null for a mixed one.</item>
    /// </list>
    /// </summary>
    /// <param name="canonicalAddress">The message's relay-decoded sender, when known; rules test it and the raw address.</param>
    public PolicyMatch? Match(MessageRow m, SenderPolicyRow policy, string? canonicalAddress = null)
    {
        var approved = policy.Rules.Where(r => r.Status == PolicyStatus.Approved).OrderBy(r => r.Position);
        var rule = CostOrder(approved).FirstOrDefault(r => Holds(r.Match, m, canonicalAddress));

        if ((rule is null || rule.Action == PolicyAction.Delete) && guard.IsTransactional(m))
        {
            return policy.IsMixed
                ? null
                : Default(policy) with
                {
                    Action = policy.Action == PolicyAction.Delete ? PolicyAction.Archive : policy.Action,
                    Guarded = true,
                };
        }

        if (rule is not null)
        {
            return new PolicyMatch(rule, rule.TopicLabel, rule.DocumentTypeLabel, rule.MailType, rule.RetentionDays, rule.Action, false);
        }

        return policy.IsMixed ? null : Default(policy);
    }

    /// <summary>
    /// Sorts rules stably by the cost of their dearest set field: header presence, then address or subdomain, then
    /// category, then subject template, then subject contains. Rules of one class keep their input order.
    /// </summary>
    public static IEnumerable<SenderPolicyRuleRow> CostOrder(IEnumerable<SenderPolicyRuleRow> rules) =>
        rules.OrderBy(r => CostClass(r.Match));

    internal static int CostClass(RuleMatch match) =>
        !string.IsNullOrWhiteSpace(match.SubjectContains) ? 4
        : !string.IsNullOrWhiteSpace(match.SubjectTemplate) ? 3
        : match.Category is not null ? 2
        : !string.IsNullOrWhiteSpace(match.FromAddress) || !string.IsNullOrWhiteSpace(match.FromSubdomain) ? 1
        : 0;

    private static PolicyMatch Default(SenderPolicyRow policy) =>
        new(null, policy.TopicLabel ?? "", policy.DocumentTypeLabel, policy.MailType, policy.RetentionDays, policy.Action, false);

    private static bool Holds(RuleMatch match, MessageRow m, string? canonicalAddress)
    {
        if (match.IsEmpty)
        {
            return false;
        }

        if (match.ListIdPresent is { } listId && listId != !string.IsNullOrWhiteSpace(m.ListId))
        {
            return false;
        }

        if (match.ListUnsubscribePresent is { } unsubscribe && unsubscribe != !string.IsNullOrWhiteSpace(m.ListUnsubscribe))
        {
            return false;
        }

        if (Set(match.FromAddress) is { } address
            && !string.Equals(address, canonicalAddress?.Trim(), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(address, m.FromAddress.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Set(match.FromSubdomain)?.TrimStart('.') is { } subdomain && !UnderDomain(m.FromAddress, subdomain))
        {
            return false;
        }

        if (match.Category is { } category && m.Category != category)
        {
            return false;
        }

        if (Set(match.SubjectTemplate) is { } template
            && !string.Equals(template, SubjectNormaliser.Template(m.Subject), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Set(match.SubjectContains) is not { } contains
            || (m.Subject?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string? Set(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // A label-boundary suffix: "news.example.com" is under "example.com", "badexample.com" is not.
    private static bool UnderDomain(string address, string subdomain)
    {
        var at = address.LastIndexOf('@');
        var domain = (at < 0 ? address : address[(at + 1)..]).Trim();
        return domain.Equals(subdomain, StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith("." + subdomain, StringComparison.OrdinalIgnoreCase);
    }
}
