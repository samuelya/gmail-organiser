using System.Text.RegularExpressions;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Rules;

/// <summary>
/// Filters to propose from approved sender policies (#373, DESIGN §6.5): for each policy no active filter covers, one
/// for a non-mixed policy's default, or one per approved rule of a mixed policy. Gmail applies every matching filter
/// where <see cref="PolicyMatcher"/> applies the first rule, so a rule's filter excludes the criteria of the rules
/// before it. Proposals that differ only in their senders are merged into <c>from:(a OR b)</c> within
/// <see cref="FilterCriteriaLimits"/>. A delete outcome is the delete label and skip inbox, never Trash, and excludes
/// mail the transactional guard would spare.
/// </summary>
public sealed partial class PolicyFilterProposalQuery(AppDbContext db, ISettingsStore settingsStore, IOptions<PolicyOptions> options)
{
    private const string PartialNote = "Gmail filters can't test {0}; this filter matches without that condition";

    /// <summary>Every policy proposal, most messages first.</summary>
    public async Task<IReadOnlyList<FilterProposalDto>> ListAsync(CancellationToken ct)
    {
        var policies = await db.SenderPolicies.AsNoTracking().Include(p => p.Rules)
            .Where(p => p.Status == PolicyStatus.Approved)
            .OrderBy(p => p.ScopeKey)
            .ToListAsync(ct);
        if (policies.Count == 0)
        {
            return [];
        }

        string[] senderKeys = [.. policies.Where(p => p.Scope == PolicyScope.Sender).Select(p => p.ScopeKey)];
        string[] domainKeys = [.. policies.Where(p => p.Scope == PolicyScope.Domain).Select(p => p.ScopeKey)];
        var senders = await db.Senders.AsNoTracking()
            .Where(s => senderKeys.Contains(s.CanonicalAddress) || domainKeys.Contains(s.CanonicalDomain))
            .Select(s => new RawSender(s.Address, s.CanonicalAddress, s.CanonicalDomain, s.TotalCount))
            .ToListAsync(ct);
        var settings = await settingsStore.GetAsync(ct);
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, [.. senders.Select(s => s.Address)], ct);
        var active = (await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct)).ConvertAll(r => r.ReadCriteria());
        var fromTerms = active.Select(c => c.From).OfType<string>().SelectMany(f => FilterCriteriaMapping.FromTerms(f) ?? []).ToList();
        var listTerms = active.Select(c => c.Query).OfType<string>()
            .SelectMany(q => ListTerm().Matches(q).Select(m => m.Groups[1].Value.Trim('<', '>').ToLowerInvariant()))
            .ToHashSet(StringComparer.Ordinal);
        var negation = Negation(options.Value.TransactionalKeywords);

        var units = new List<Unit>();
        foreach (var policy in policies)
        {
            var scope = await ScopeAsync(policy, senders, senderKeys, ct);
            var covered = policy.Scope == PolicyScope.List
                ? listTerms.Contains(ListKey(policy.ScopeKey))
                : scope.Addresses.All(a => fromTerms.Any(t => FilterCriteriaMapping.FromTermMatches(t, a)));
            if (covered)
            {
                continue;
            }

            var allowlisted = scope.Addresses.Concat(scope.From).Select(allowlist.Reason).OfType<string>().FirstOrDefault();
            if (!policy.IsMixed)
            {
                // Rules left on a single-label policy are ignored (PolicyMatcher.FirstRule).
                if (Build(policy, null, scope, Criteria(new RuleMatch()), [], allowlisted, negation, settings) is { } single)
                {
                    units.Add(single);
                }

                continue;
            }

            // A mixed policy's unmatched mail goes to review, so its default gets no filter.
            var earlier = new List<Terms>();
            foreach (var rule in policy.Rules.Where(r => r.Status == PolicyStatus.Approved).OrderBy(r => r.Position))
            {
                var terms = Criteria(rule.Match);
                if (Build(policy, rule, scope, terms, earlier, allowlisted, negation, settings) is { } unit)
                {
                    units.Add(unit);
                }

                earlier.Add(terms);
            }
        }

        return [.. Merge(units).OrderByDescending(p => p.MessageCount).ThenBy(p => p.SenderAddress, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The scope's <c>from</c> terms or <c>list:</c> query. A relay address (hide-my-email) carries the relay's domain in
    /// its From header, so <c>from:@&lt;canonical domain&gt;</c> would not match it: the raw addresses behind the
    /// canonical one are the only criteria that work. A domain's non-relay senders are covered by <c>@domain</c>; a
    /// relay address with its own sender policy is left to that policy's filter.
    /// </summary>
    private async Task<Scope> ScopeAsync(SenderPolicyRow policy, List<RawSender> senders, string[] senderKeys, CancellationToken ct)
    {
        if (policy.Scope == PolicyScope.List)
        {
            var key = policy.ScopeKey;
            var count = await db.Messages.CountAsync(m => m.ListId != null && m.ListId.ToLower() == key, ct);
            return new Scope([], [], ListKey(key), count);
        }

        var raw = policy.Scope == PolicyScope.Sender
            ? senders.Where(s => s.CanonicalAddress == policy.ScopeKey).ToList()
            : senders.Where(s => s.CanonicalDomain == policy.ScopeKey && !senderKeys.Contains(s.CanonicalAddress)).ToList();
        List<string> addresses = [.. raw.Select(s => s.Address.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)];
        List<string> terms;
        if (policy.Scope == PolicyScope.Sender)
        {
            terms = addresses.Count > 0 ? addresses : [policy.ScopeKey.ToLowerInvariant()];
        }
        else
        {
            var domain = "@" + policy.ScopeKey.ToLowerInvariant();
            var relays = addresses.Where(a => !FilterCriteriaMapping.FromTermMatches(domain, a));
            terms = [domain, .. relays];
        }

        return new Scope(terms, addresses.Count > 0 ? addresses : terms, null, raw.Sum(s => s.TotalCount));
    }

    /// <summary>The criteria of a rule's own match fields, apart from the scope.</summary>
    private static Terms Criteria(RuleMatch match)
    {
        var query = new List<string>();
        var notes = new List<string>();
        var dropped = new List<string>();
        var wider = false;
        var from = Set(match.FromAddress)?.ToLowerInvariant()
            ?? (Set(match.FromSubdomain)?.TrimStart('.', '@') is { } subdomain ? "@" + subdomain.ToLowerInvariant() : null);
        if (match.Category is { } category)
        {
            query.Add("category:" + category.ToString().ToLowerInvariant());
        }

        if (Set(match.SubjectTemplate) is { } template)
        {
            var literal = Literal(template);
            if (literal.Length == 0)
            {
                dropped.Add("a subject template without literal text");
            }
            else
            {
                query.Add($"subject:\"{literal}\"");
                if (literal != template.Trim())
                {
                    wider = true;
                    notes.Add($"Subject matched by the template's literal part \"{literal}\"");
                }
            }
        }

        if (Phrase(match.SubjectContains) is { Length: > 0 } contains)
        {
            query.Add($"subject:\"{contains}\"");
        }

        if (match.ListIdPresent is not null)
        {
            dropped.Add("whether a List-Id is present");
        }

        if (match.ListUnsubscribePresent is not null)
        {
            dropped.Add("whether List-Unsubscribe is present");
        }

        if (dropped.Count > 0)
        {
            notes.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, PartialNote, string.Join(", ", dropped)));
        }

        return new Terms(from, query, notes, wider || dropped.Count > 0);
    }

    /// <summary>
    /// The unit for a policy's default (no <paramref name="rule"/>) or one rule, or null when the rules before it can't
    /// be excluded within the length cap: a rule with no criteria Gmail can test would exclude the whole scope.
    /// </summary>
    private static Unit? Build(
        SenderPolicyRow policy,
        SenderPolicyRuleRow? rule,
        Scope scope,
        Terms terms,
        List<Terms> earlier,
        string? allowlisted,
        string negation,
        AppSettings settings)
    {
        List<string> from = terms.From is { } own ? [own] : scope.From;
        var query = new List<string>();
        List<string> notes = [.. terms.Notes];
        if (scope.ListId is { } list)
        {
            query.Add("list:" + list);
        }

        query.AddRange(terms.Query);
        foreach (var before in earlier)
        {
            // A wider earlier filter excludes more than its rule matches: this filter then misses some mail, never adds any.
            if (Exclusion(before) is not { } exclusion)
            {
                return null;
            }

            query.Add(exclusion);
        }

        if (earlier.Count > 0)
        {
            notes.Add("Excludes the mail of the policy's earlier rules");
        }

        if (!FitsAlone(from, query))
        {
            return null;
        }

        var outcome = rule is null
            ? (Topic: policy.TopicLabel ?? "", Type: policy.DocumentTypeLabel, policy.MailType, policy.Action)
            : (Topic: rule.TopicLabel, Type: rule.DocumentTypeLabel, rule.MailType, rule.Action);
        var delete = outcome.Action == PolicyAction.Delete;
        if (delete && terms.Wider)
        {
            // A dropped condition or a template's literal part widens the filter beyond what the rule deletes.
            delete = false;
            notes.Add("Archives instead of marking for deletion because the filter is wider than the rule");
        }
        else if (delete && allowlisted is not null)
        {
            delete = false;
            notes.Add($"Archives instead of marking for deletion ({allowlisted})");
        }
        else if (delete && !FitsAlone(from, [.. query, negation]))
        {
            delete = false;
            notes.Add("Archives instead of marking for deletion because excluding every transactional keyword would make the filter too long");
        }

        if (delete)
        {
            query.Add(negation);
        }

        var needsAction = outcome.MailType == MailType.ActionBill;
        var labels = new List<string> { outcome.Topic };
        if (outcome.Type is { } type && ActionPlanner.AppliesDocumentType(type, settings))
        {
            labels.Add(type);
        }

        if (needsAction)
        {
            labels.Add(settings.ActionLabelName);
        }

        if (delete)
        {
            labels.Add(settings.DeleteLabelName);
        }

        // The apply job's inbox rule (ActionPlanner): action mail stays, keep stays unless it is deleted.
        var skipInbox = !needsAction && (outcome.Action != PolicyAction.Keep || delete);
        var action = new FilterActionRequest([.. labels.Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)], skipInbox, false);
        var pattern = new SenderPatternDto(outcome.Topic, needsAction, delete, 0, 1, 0, outcome.Type);
        return new Unit(policy, rule, from, string.Join(' ', query), action, pattern, terms.Wider, notes, scope.MessageCount);
    }

    /// <summary><c>-term</c> or <c>-(a b)</c> for an earlier rule's criteria, or null when it has none Gmail can test.</summary>
    private static string? Exclusion(Terms terms)
    {
        List<string> parts = [.. terms.From is { } from ? ["from:" + from] : Array.Empty<string>(), .. terms.Query];
        return parts.Count switch
        {
            0 => null,
            1 => "-" + parts[0],
            _ => $"-({string.Join(' ', parts)})",
        };
    }

    /// <summary>Whether a unit fits the length cap with its longest <c>from</c> term, the least a merged chunk holds.</summary>
    private static bool FitsAlone(List<string> from, List<string> query) =>
        FilterCriteriaMapping.ToQuery(new(From: from.MaxBy(t => t.Length), Query: string.Join(' ', query))).Length
            <= FilterCriteriaLimits.MaxQueryChars;

    /// <summary>
    /// Units with the same query and action become one <c>from:(a OR b)</c> per run of terms that fits
    /// <see cref="FilterCriteriaLimits"/>; a partial unit keeps its own filter so its note stays its own.
    /// </summary>
    private static IEnumerable<FilterProposalDto> Merge(List<Unit> units)
    {
        foreach (var group in units.GroupBy(u => (
            u.Query, string.Join('\n', u.Action.AddLabelNames ?? []), u.Action.SkipInbox, u.From.Count == 0, Own: u.Partial ? (u.Policy.Id, u.Rule?.Id) : default)))
        {
            var members = group.ToList();
            if (members[0].From.Count == 0)
            {
                foreach (var unit in members)
                {
                    yield return Dto(unit, [unit], null);
                }

                continue;
            }

            var chunk = new List<string>();
            var chunkUnits = new List<Unit>();
            foreach (var unit in members)
            {
                foreach (var term in unit.From)
                {
                    if (chunk.Contains(term, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    if (chunk.Count > 0 && !Fits([.. chunk, term], unit.Query))
                    {
                        yield return Dto(chunkUnits[0], chunkUnits, string.Join(" OR ", chunk));
                        chunk = [];
                        chunkUnits = [];
                    }

                    chunk.Add(term);
                    if (!chunkUnits.Contains(unit))
                    {
                        chunkUnits.Add(unit);
                    }
                }
            }

            if (chunk.Count > 0)
            {
                yield return Dto(chunkUnits[0], chunkUnits, string.Join(" OR ", chunk));
            }
        }
    }

    private static bool Fits(List<string> terms, string query) =>
        terms.Count <= FilterCriteriaLimits.MaxFromTerms
        && FilterCriteriaMapping.ToQuery(new(From: string.Join(" OR ", terms), Query: query)).Length <= FilterCriteriaLimits.MaxQueryChars;

    private static FilterProposalDto Dto(Unit first, List<Unit> units, string? from)
    {
        var policies = units.Select(u => u.Policy.Id).Distinct().Count();
        List<string> notes = [.. units.SelectMany(u => u.Notes).Distinct(StringComparer.Ordinal)];
        if (policies > 1)
        {
            notes.Insert(0, $"Merges the filters of {policies} policies");
        }

        var criteria = new FilterCriteriaDto(from, null, null, first.Query.Length > 0 ? first.Query : null, null, null, null, null, null);
        return new FilterProposalDto(
            first.Policy.ScopeKey,
            first.Rule?.Name ?? first.Policy.DisplayName,
            units.DistinctBy(u => u.Policy.Id).Sum(u => u.MessageCount),
            first.Policy.Scope == PolicyScope.List ? first.Policy.ScopeKey : null,
            first.Pattern,
            new FilterSuggestionDto(criteria, first.Action),
            FilterProposalSources.Policy,
            first.Policy.Id,
            units.Count == 1 ? first.Rule?.Id : null,
            first.Partial,
            notes.Count > 0 ? string.Join("; ", notes) + "." : null);
    }

    /// <summary>
    /// <c>-has:attachment -a -"b c"</c> over every keyword. A bare term searches the whole message, so it excludes at least
    /// what <see cref="TransactionalGuard"/> spares by subject or snippet.
    /// </summary>
    private static string Negation(IEnumerable<string> keywords) =>
        string.Join(' ', keywords.Select(Phrase).OfType<string>().Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(k => k.Contains(' ', StringComparison.Ordinal) ? $"-\"{k}\"" : "-" + k)
            .Prepend("-has:attachment"));

    /// <summary>The longest run of a <c>SubjectNormaliser</c> template between its <c>#</c> placeholders.</summary>
    private static string Literal(string template) =>
        template.Split('#').Select(Phrase).OfType<string>().MaxBy(s => s.Length) ?? "";

    /// <summary>Text safe inside a quoted Gmail phrase: no quotes or brackets, single spaces, trimmed of punctuation.</summary>
    private static string? Phrase(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
            : string.Join(' ', text.Split([' ', '\t', '"', '(', ')', '{', '}'], StringSplitOptions.RemoveEmptyEntries))
                .Trim(' ', '-', ':', ',', '.', ';', '|', '/');

    private static string? Set(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ListKey(string listId) => listId.Trim().Trim('<', '>').ToLowerInvariant();

    [GeneratedRegex(@"(?<![\w-])list:(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListTerm();

    /// <param name="From">A rule's own <c>from</c> term, replacing the scope's.</param>
    /// <param name="Wider">Whether the filter matches more than the rule: a dropped condition or a template's literal part.</param>
    private sealed record Terms(string? From, List<string> Query, List<string> Notes, bool Wider);

    private sealed record RawSender(string Address, string CanonicalAddress, string CanonicalDomain, int TotalCount);

    /// <param name="From">The <c>from</c> terms; empty for a list scope.</param>
    /// <param name="Addresses">What decides whether an active filter covers the scope.</param>
    private sealed record Scope(List<string> From, List<string> Addresses, string? ListId, int MessageCount);

    private sealed record Unit(
        SenderPolicyRow Policy,
        SenderPolicyRuleRow? Rule,
        List<string> From,
        string Query,
        FilterActionRequest Action,
        SenderPatternDto Pattern,
        bool Partial,
        List<string> Notes,
        int MessageCount);
}
