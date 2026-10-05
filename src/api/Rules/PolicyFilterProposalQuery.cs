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
/// for a non-mixed policy's default, or one per approved rule of a mixed policy. Gmail search can't reproduce
/// <see cref="PolicyMatcher"/> (whole words, no relay-decoded addresses, no first-match order), so only a filter that is
/// at least as narrow as the policy archives or marks for deletion; any other filter only adds the topic label, and the
/// portal's fetch coverage applies the real outcome. A mixed policy's rule filters are always label-only. A non-mixed
/// policy's filter takes its outcome only when it excludes the narrower policies' senders and lists and, when it
/// archives or deletes, the allowlisted senders and every transactional keyword, within <see cref="FilterCriteriaLimits"/>.
/// Proposals that differ only in their senders are merged into <c>from:(a OR b)</c>. A delete outcome is the delete label
/// and skip inbox, never Trash.
/// </summary>
public sealed partial class PolicyFilterProposalQuery(AppDbContext db, ISettingsStore settingsStore, IOptions<PolicyOptions> options)
{
    private const string PartialNote = "Gmail filters can't test {0}; this filter matches without that condition";
    private const string MixedNote = "Labels only: Gmail can't apply a mixed policy's first matching rule, so the portal applies the rule's action when it fetches the mail";

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
        var listKeys = policies.Where(p => p.Scope == PolicyScope.List).Select(p => ListKey(p.ScopeKey)).ToHashSet(StringComparer.Ordinal);
        var senders = await db.Senders.AsNoTracking()
            .Where(s => senderKeys.Contains(s.CanonicalAddress) || domainKeys.Contains(s.CanonicalDomain))
            .Select(s => new RawSender(s.Address, s.CanonicalAddress, s.CanonicalDomain, s.TotalCount))
            .ToListAsync(ct);
        var settings = await settingsStore.GetAsync(ct);

        // Every allowlisted address: any of them can post to a list.
        var allowlist = await AllowlistLoader.LoadAsync(db, settings, ct);
        var active = (await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct)).ConvertAll(r => r.ReadCriteria());
        var fromTerms = active.Select(c => c.From).OfType<string>().SelectMany(f => FilterCriteriaMapping.FromTerms(f) ?? []).ToList();
        var listTerms = active.Select(c => c.Query).OfType<string>()
            .SelectMany(q => ListTerm().Matches(q).Select(m => m.Groups[1].Value.Trim('<', '>').ToLowerInvariant()))
            .ToHashSet(StringComparer.Ordinal);
        var negation = Negation(options.Value.TransactionalKeywords);

        var units = new List<Unit>();
        foreach (var policy in policies)
        {
            var scope = await ScopeAsync(policy, senders, senderKeys, listKeys, allowlist, ct);
            var covered = policy.Scope == PolicyScope.List
                ? listTerms.Contains(ListKey(policy.ScopeKey))
                : scope.Addresses.All(a => fromTerms.Any(t => FilterCriteriaMapping.FromTermMatches(t, a)));
            if (covered)
            {
                continue;
            }

            if (!policy.IsMixed)
            {
                // Rules left on a single-label policy are ignored (PolicyMatcher.FirstRule).
                units.Add(Default(policy, scope, negation, settings));
                continue;
            }

            // A mixed policy's unmatched mail goes to review, so its default gets no filter.
            foreach (var rule in policy.Rules.Where(r => r.Status == PolicyStatus.Approved).OrderBy(r => r.Position))
            {
                if (RuleUnit(policy, rule, scope) is { } unit)
                {
                    units.Add(unit);
                }
            }
        }

        return [.. Merge(units).OrderByDescending(p => p.MessageCount).ThenBy(p => p.SenderAddress, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The scope's <c>from</c> terms or <c>list:</c> query, and what its filter must exclude. A relay address
    /// (hide-my-email) carries the relay's domain in its From header, so <c>from:@&lt;canonical domain&gt;</c> would not
    /// match it: the raw addresses behind the canonical one are the only criteria that work. A domain's non-relay senders
    /// are covered by <c>@domain</c>; a relay address with its own sender policy is left to that policy's filter. A message's
    /// policy is its sender's, else its list's, else its domain's (<see cref="PolicyLookup.For"/>), so a list filter excludes
    /// the senders on it with a sender policy, and a domain filter those senders and the lists its mail is on.
    /// </summary>
    private async Task<Scope> ScopeAsync(
        SenderPolicyRow policy, List<RawSender> senders, string[] senderKeys, HashSet<string> listKeys, Allowlist allowlist, CancellationToken ct)
    {
        var key = policy.ScopeKey;
        if (policy.Scope == PolicyScope.List)
        {
            var count = await db.Messages.CountAsync(m => m.ListId != null && m.ListId.ToLower() == key, ct);
            var winners = await db.Messages.AsNoTracking()
                .Where(m => m.ListId != null && m.ListId.ToLower() == key && senderKeys.Contains(m.CanonicalAddress))
                .Select(m => m.FromAddress.ToLower())
                .Distinct()
                .ToListAsync(ct);
            List<string> allowed = [.. allowlist.Addresses, .. allowlist.Domains.Select(d => "@" + d)];
            return new Scope([], [], "list:" + ListKey(key), Excluding("from", winners), Excluding("from", allowed), null, count);
        }

        var raw = policy.Scope == PolicyScope.Sender
            ? senders.Where(s => s.CanonicalAddress == key).ToList()
            : senders.Where(s => s.CanonicalDomain == key && !senderKeys.Contains(s.CanonicalAddress)).ToList();
        List<string> addresses = [.. raw.Select(s => s.Address.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)];
        if (policy.Scope == PolicyScope.Sender)
        {
            List<string> terms = addresses.Count > 0 ? addresses : [key.ToLowerInvariant()];
            var reasons = terms.Select(allowlist.Reason).ToList();
            var whole = reasons.All(r => r is not null) ? reasons[0] : null;
            List<string> allowed = whole is null ? [.. terms.Where((_, i) => reasons[i] is not null)] : [];
            return new Scope(terms, addresses.Count > 0 ? addresses : terms, null, [], Excluding("from", allowed), whole, raw.Sum(s => s.TotalCount));
        }

        var domain = "@" + key.ToLowerInvariant();
        var relays = addresses.Where(a => !FilterCriteriaMapping.FromTermMatches(domain, a));
        var senderWinners = senders.Where(s => s.CanonicalDomain == key && senderKeys.Contains(s.CanonicalAddress))
            .Select(s => s.Address.ToLowerInvariant());
        var lists = (await db.Messages.AsNoTracking()
                .Where(m => m.CanonicalDomain == key && m.ListId != null)
                .Select(m => m.ListId!.ToLower())
                .Distinct()
                .ToListAsync(ct))
            .Select(ListKey)
            .Where(listKeys.Contains);
        List<string> domainAllowed =
        [
            .. addresses.Where(a => allowlist.Reason(a) is not null),
            .. allowlist.Addresses.Where(a => FilterCriteriaMapping.FromTermMatches(domain, a)),
            .. allowlist.Domains.Where(d => d.EndsWith(domain.Replace('@', '.'), StringComparison.Ordinal)).Select(d => "@" + d),
        ];
        return new Scope(
            [domain, .. relays],
            addresses.Count > 0 ? addresses : [domain],
            null,
            [.. Excluding("from", senderWinners), .. Excluding("list", lists)],
            Excluding("from", domainAllowed),
            Allowlist.CoversDomain(allowlist.Domains, key) ? Allowlist.DomainReason : null,
            raw.Sum(s => s.TotalCount));
    }

    private static List<string> Excluding(string op, IEnumerable<string> values) =>
        [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(v => $"-{op}:{v}")];

    /// <summary>
    /// A non-mixed policy's filter: its outcome when the narrower policies (and, for archive or delete, the allowlisted
    /// senders and every transactional keyword) can be excluded within the length cap; otherwise the topic label only.
    /// </summary>
    private static Unit Default(SenderPolicyRow policy, Scope scope, string negation, AppSettings settings)
    {
        var leaves = policy.Action != PolicyAction.Keep;
        List<string> query = [.. scope.Base, .. scope.Narrower];
        string? why = null;
        if (leaves && scope.Protected is { } reason)
        {
            why = $"its senders are protected ({reason})";
        }
        else
        {
            if (leaves)
            {
                query.AddRange([.. scope.Allowlisted, negation]);
            }

            if (!FitsAlone(scope.From, query))
            {
                why = leaves
                    ? "excluding narrower policies, allowlisted senders and every transactional keyword would make the filter too long"
                    : "excluding narrower policies would make the filter too long";
            }
        }

        if (why is not null)
        {
            return LabelOnly(policy, null, scope, scope.Base, [$"Labels only because {why}; the portal applies the policy's action when it fetches the mail"], false);
        }

        List<string> notes = scope.Narrower.Count > 0 ? ["Excludes the mail of narrower policies"] : [];
        var delete = policy.Action == PolicyAction.Delete;
        var needsAction = policy.MailType == MailType.ActionBill;
        var topic = policy.TopicLabel ?? "";
        var labels = new List<string> { topic };
        if (policy.DocumentTypeLabel is { } type && ActionPlanner.AppliesDocumentType(type, settings))
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

        // The apply job's inbox rule (ActionPlanner): action mail stays, keep stays.
        var action = new FilterActionRequest([.. labels.Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)], !needsAction && leaves, false);
        var pattern = new SenderPatternDto(topic, needsAction, delete, 0, 1, 0, policy.DocumentTypeLabel);
        return new Unit(policy, null, scope.From, string.Join(' ', query), action, pattern, false, notes, scope.MessageCount);
    }

    /// <summary>A mixed policy's rule as a label-only filter, or null when Gmail can test none of its conditions.</summary>
    private static Unit? RuleUnit(SenderPolicyRow policy, SenderPolicyRuleRow rule, Scope scope)
    {
        var terms = Criteria(rule.Match);
        List<string> query = [.. scope.Base, .. terms.Query];

        // With no testable condition the filter would label the whole scope.
        return terms.Query.Count == 0 || !FitsAlone(scope.From, query)
            ? null
            : LabelOnly(policy, rule, scope, query, [.. terms.Notes, MixedNote], terms.Wider);
    }

    private static Unit LabelOnly(SenderPolicyRow policy, SenderPolicyRuleRow? rule, Scope scope, List<string> query, List<string> notes, bool wider)
    {
        var topic = rule?.TopicLabel ?? policy.TopicLabel ?? "";
        var action = new FilterActionRequest(topic.Length > 0 ? [topic] : [], false, false);
        var pattern = new SenderPatternDto(topic, false, false, 0, 1, 0, null);
        return new Unit(policy, rule, scope.From, string.Join(' ', query), action, pattern, wider, notes, scope.MessageCount);
    }

    /// <summary>
    /// The criteria of a rule's own match fields. A <c>from</c> condition only narrows the scope's senders, so it goes
    /// into the query rather than replacing them.
    /// </summary>
    private static Terms Criteria(RuleMatch match)
    {
        var query = new List<string>();
        var notes = new List<string>();
        var dropped = new List<string>();
        var wider = false;
        if (Set(match.FromAddress) is { } address)
        {
            query.Add("from:" + address.ToLowerInvariant());
        }
        else if (Set(match.FromSubdomain)?.TrimStart('.', '@') is { Length: > 0 } subdomain)
        {
            query.Add("from:@" + subdomain.ToLowerInvariant());
        }

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
                // Gmail tests containment; the matcher tests template equality.
                query.Add($"subject:\"{literal}\"");
                wider = true;
                notes.Add($"Subject matched by containing \"{literal}\", not by the template");
            }
        }

        if (Set(match.SubjectContains) is { } text)
        {
            if (Phrase(text) is { Length: > 0 } contains)
            {
                query.Add($"subject:\"{contains}\"");
            }
            else
            {
                dropped.Add($"the subject text \"{text}\"");
            }
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

        return new Terms(query, notes, wider || dropped.Count > 0);
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

    /// <param name="Wider">Whether the filter matches more than the rule: a dropped condition or a subject template.</param>
    private sealed record Terms(List<string> Query, List<string> Notes, bool Wider);

    private sealed record RawSender(string Address, string CanonicalAddress, string CanonicalDomain, int TotalCount);

    /// <param name="From">The <c>from</c> terms; empty for a list scope.</param>
    /// <param name="Addresses">What decides whether an active filter covers the scope.</param>
    /// <param name="List">The <c>list:</c> term of a list scope.</param>
    /// <param name="Narrower">Exclusions of the mail a narrower policy decides.</param>
    /// <param name="Allowlisted">Exclusions of the allowlisted senders in the scope.</param>
    /// <param name="Protected">Why the whole scope is allowlisted, or null.</param>
    private sealed record Scope(
        List<string> From, List<string> Addresses, string? List, List<string> Narrower, List<string> Allowlisted, string? Protected, int MessageCount)
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
        int MessageCount);
}
