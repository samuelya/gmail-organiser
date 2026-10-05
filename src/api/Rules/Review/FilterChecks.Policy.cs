using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Rules.Review;

/// <summary>
/// The consolidation checks against the policy proposals (#374). A filter is within a proposal when the proposal matches
/// at least what the filter matches: each of the filter's <c>from</c> terms is one of the proposal's, or under one of its
/// <c>@domain</c>s and not excluded by a <c>-from:</c>, and the filter has every other token of the proposal's query
/// (compared after <see cref="PolicyFilterProposalQuery.CriteriaTerms"/>). A fix always creates the proposal as #373 built it, so
/// it never adds the delete label, skips the inbox only where #373 allows and fits the length cap. A proposal whose label
/// the mailbox doesn't have yet, or a filter only partly within one, gets no finding.
/// </summary>
public static partial class FilterChecks
{
    private static IEnumerable<FilterFindingDraft> PolicyFindings(
        List<Parsed> filters, IReadOnlyList<FilterProposalDto> proposals, IReadOnlyList<GmailLabel> labels, Dictionary<string, string> names)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels.Where(l => l.Type == GmailLabelType.User))
        {
            ids.TryAdd(label.Name, label.Id);
        }

        var userIds = ids.Values.ToHashSet(StringComparer.Ordinal);

        var targets = proposals.Select(p => Target(p, ids)).OfType<PolicyTarget>().ToList();
        if (targets.Count == 0)
        {
            yield break;
        }

        var overlaps = new List<(PolicyTarget Target, List<Parsed> Members)>();
        foreach (var f in filters.Where(f => !f.Forwards && f.Missing.Count == 0))
        {
            var (from, tokens) = PolicyFilterProposalQuery.CriteriaTerms(f.Criteria);
            var within = targets.Where(t => Within(from, tokens, t)).ToList();

            // A filter that is a proposal already (same criteria and action) is the policy's own filter.
            if (within.Any(t => t.ActionKey == f.ActionKey && t.From.SequenceEqual(from) && t.Tokens.SequenceEqual(tokens)))
            {
                continue;
            }

            if (within.FirstOrDefault(t => t.ActionKey == f.ActionKey) is { } same)
            {
                if (overlaps.FirstOrDefault(o => ReferenceEquals(o.Target, same)) is { Members: { } members })
                {
                    members.Add(f);
                }
                else
                {
                    overlaps.Add((same, [f]));
                }

                continue;
            }

            if (within.Select(t => (Target: t, Against: Against(f.Action, t.Action, userIds, names))).FirstOrDefault(c => c.Against is not null)
                is { Target: { } target, Against: { } against })
            {
                var text = $"{Quote([f])} {against} for the mail of the policy for '{target.Proposal.SenderAddress}', whose filter "
                    + $"'{target.Query}' would {Describe(target.Action, names)}; replace it with the policy's filter.";
                if (f.HidesMail)
                {
                    text += " The policy's filter never trashes: the portal marks mail for deletion, with its protections.";
                }

                if (!target.Action.RemoveLabelIds.Contains(FilterSpec.Inbox, StringComparer.Ordinal))
                {
                    text += " It only labels; the portal applies the policy's action.";
                }

                yield return new(
                    FilterFindingKind.PolicyConflict,
                    [f.Row.Id],
                    text,
                    new FilterFix(FilterFixKind.Relabel, [f.Row.Id], new(target.Criteria, target.Action), target.Proposal.PolicyId));
            }
        }

        foreach (var (target, members) in overlaps)
        {
            yield return new(
                FilterFindingKind.OverlapsPolicy,
                [.. members.Select(f => f.Row.Id)],
                $"{Quote(members)} {(members.Count == 1 ? "is" : "are")} within the filter the policy for '{target.Proposal.SenderAddress}' "
                + $"proposes ('{target.Query}'), which also {Describe(target.Action, names)}; that one filter can replace "
                + $"{(members.Count == 1 ? "it" : "them")}.",
                new FilterFix(FilterFixKind.Merge, [.. members.Select(f => f.Row.Id)], new(target.Criteria, target.Action), target.Proposal.PolicyId));
        }
    }

    /// <summary>The proposal with label ids, or null when it has no policy or a label the mailbox doesn't have.</summary>
    private static PolicyTarget? Target(FilterProposalDto proposal, Dictionary<string, string> ids)
    {
        if (proposal.PolicyId is null
            || FilterCriteriaMapping.TryRead(proposal.Suggested.Criteria, proposal.Suggested.Action, out _) is not { } spec
            || spec.AddLabelNames.Any(n => !ids.ContainsKey(n)))
        {
            return null;
        }

        var action = new GmailFilterAction([.. spec.AddLabelNames.Select(n => ids[n])], spec.RemoveLabelIds);
        var (from, tokens) = PolicyFilterProposalQuery.CriteriaTerms(spec.Criteria);
        return new PolicyTarget(proposal, spec.Criteria, action, ActionKey(action), from, tokens, FilterCriteriaMapping.ToQuery(spec.Criteria));
    }

    /// <summary>Whether the proposal matches at least what a filter with these terms matches.</summary>
    private static bool Within(IReadOnlyList<string> from, IReadOnlyList<string> tokens, PolicyTarget target)
    {
        // An OR or a {…} group in the filter's query widens it, so its tokens no longer only narrow.
        if (tokens.Any(t => t == "or" || t.StartsWith('{')) || from.Any(t => t.Length == 0))
        {
            return false;
        }

        var excluded = target.Tokens.Where(t => t.StartsWith("-from:", StringComparison.Ordinal) && t.Length > 6).Select(t => t[6..]).ToList();
        if (from.Count == 0 ? target.From.Count > 0 : !from.All(e => Covers(target.From, excluded, e)))
        {
            return false;
        }

        // The filter's own from terms are checked against the proposal's exclusions above.
        return target.Tokens.Where(t => from.Count == 0 || !t.StartsWith("-from:", StringComparison.Ordinal)).All(t => tokens.Contains(t, StringComparer.Ordinal));
    }

    private static bool Covers(IReadOnlyList<string> proposalFrom, List<string> excluded, string term) =>
        proposalFrom.Any(p => p == term || (p[0] == '@' && FilterCriteriaMapping.FromTermMatches(p, term)))
        && !excluded.Any(x => x == term || FilterCriteriaMapping.FromTermMatches(x, term) || FilterCriteriaMapping.FromTermMatches(term, x));

    /// <summary>How a filter's action goes against the proposal's: another label, Trash or Spam, or skipping the inbox; null when it doesn't.</summary>
    private static string? Against(GmailFilterAction filter, GmailFilterAction proposal, HashSet<string> userIds, Dictionary<string, string> names)
    {
        if (filter.AddLabelIds.Any(id => id is MailboxFetchJob.TrashLabelId or MailboxFetchJob.SpamLabelId))
        {
            return "sends to Trash or Spam";
        }

        var other = filter.AddLabelIds.Where(id => userIds.Contains(id) && !proposal.AddLabelIds.Contains(id, StringComparer.Ordinal)).ToList();
        if (other.Count > 0)
        {
            return $"adds {string.Join(", ", other.Select(id => names.GetValueOrDefault(id, id)))}";
        }

        return filter.RemoveLabelIds.Contains(FilterSpec.Inbox, StringComparer.Ordinal) && !proposal.RemoveLabelIds.Contains(FilterSpec.Inbox, StringComparer.Ordinal)
            ? "skips the inbox"
            : null;
    }

    private sealed record PolicyTarget(
        FilterProposalDto Proposal, GmailFilterCriteria Criteria, GmailFilterAction Action, string ActionKey,
        IReadOnlyList<string> From, IReadOnlyList<string> Tokens, string Query);
}
