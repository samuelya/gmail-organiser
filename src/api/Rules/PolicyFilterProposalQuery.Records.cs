using GmailOrganiser.Policies;
using GmailOrganiser.Review;

namespace GmailOrganiser.Rules;

/// <summary>The working records of <see cref="PolicyFilterProposalQuery"/>.</summary>
public sealed partial class PolicyFilterProposalQuery
{
    /// <param name="Wider">Whether the filter matches more than the rule: a dropped condition or a subject template.</param>
    private sealed record Terms(List<string> Query, List<string> Notes, bool Wider);

    private sealed record RawSender(string Address, string CanonicalAddress, string CanonicalDomain, int TotalCount);

    /// <summary>
    /// What the portal knows for the exclusions: the policies' senders, keys and every canonical domain seen; and the
    /// stored List-Ids (loaded only with a list policy), as stored, for the list count.
    /// </summary>
    private sealed record Known(List<RawSender> Senders, string[] SenderKeys, List<string> ListKeys, List<string> Domains, List<string> ListIds)
    {
        /// <summary>The raw addresses of the sender policies whose key passes <paramref name="where"/>, or the key when none is stored.</summary>
        public IEnumerable<string> PolicyAddresses(Func<string, bool> where) =>
            SenderKeys.Where(where).SelectMany(k => Senders.Where(s => s.CanonicalAddress == k).Select(s => s.Address).DefaultIfEmpty(k))
                .Select(a => a.ToLowerInvariant());
    }

    /// <param name="From">The <c>from</c> terms; empty for a list scope.</param>
    /// <param name="List">The <c>list:</c> term of a list scope.</param>
    /// <param name="Narrower">Exclusions of the mail a narrower policy decides, or a subdomain's.</param>
    private sealed record Scope(List<string> From, string? List, List<string> Narrower, int MessageCount)
    {
        public List<string> Base => List is { } list ? [list] : [];
    }

    private sealed record Unit(
        SenderPolicyRow Policy,
        SenderPolicyRuleRow? Rule,
        List<string> From,
        string Query,
        FilterActionRequest Action,
        SenderPatternDto Pattern,
        bool Partial,
        List<string> Notes,
        int MessageCount)
    {
        /// <summary>The unit, or null when its query doesn't fit the cap: a wider filter is never proposed instead.</summary>
        public static Unit? Fitting(
            SenderPolicyRow policy, SenderPolicyRuleRow? rule, Scope scope, List<string> query, FilterActionRequest action, SenderPatternDto pattern, bool partial, List<string> notes)
        {
            if (!FitsAlone(scope.From, query))
            {
                return null;
            }

            if (scope.Narrower.Count > 0)
            {
                notes.Add(scope.List is null ? "Excludes the mail of narrower policies and known subdomains" : "Excludes the mail of narrower policies");
            }

            return new Unit(policy, rule, scope.From, string.Join(' ', query), action, pattern, partial, notes, scope.MessageCount);
        }
    }
}
