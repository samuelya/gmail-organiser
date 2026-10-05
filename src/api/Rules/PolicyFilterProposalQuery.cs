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
/// <see cref="PolicyMatcher"/> (whole words, no relay-decoded addresses, no first-match order) or
/// <see cref="MessageProtection"/>, so a proposed filter only adds the policy's topic label, never the delete label, and
/// excludes the mail of the narrower policies the portal knows (<see cref="PolicyLookup"/>: sender before list before
/// domain). It skips the inbox only where the portal's decision can't differ for any message it matches: an exact
/// sender-address, non-mixed policy that archives or deletes, with transactional mail excluded, while
/// <see cref="AppSettings.PolicyAutoApplyFetched"/> is on. Every other proposal's note says why it is label-only.
/// Proposals that differ only in their senders are merged into <c>from:(a OR b)</c>.
/// </summary>
public sealed partial class PolicyFilterProposalQuery(AppDbContext db, ISettingsStore settingsStore, IOptions<PolicyOptions> options)
{
    private const string PartialNote = "Gmail filters can't test {0}; this filter matches without that condition";
    private const string MixedNote = "Gmail can't apply a mixed policy's first matching rule";
    private const string SkipsNote = "Skips the inbox: an exact sender policy that leaves the inbox; transactional mail (attachments, keywords) stays for the portal";
    private const string DeleteLabelNote = "Doesn't add the delete label: the portal marks mail for deletion, with its protections";

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
        var known = new Known(
            senders,
            senderKeys,
            [.. policies.Where(p => p.Scope == PolicyScope.List).Select(p => ListKey(p.ScopeKey))],
            await db.Senders.AsNoTracking().Select(s => s.CanonicalDomain).Distinct().ToListAsync(ct));
        var settings = await settingsStore.GetAsync(ct);
        var active = (await db.Filters.AsNoTracking().Where(r => r.DeletedAt == null).ToListAsync(ct)).ConvertAll(r => r.ReadCriteria());
        var fromTerms = active.Select(c => c.From).OfType<string>().SelectMany(f => FilterCriteriaMapping.FromTerms(f) ?? []).ToList();
        var listTerms = active.Select(c => c.Query).OfType<string>()
            .SelectMany(q => ListTerm().Matches(q).Select(m => m.Groups[1].Value.Trim('<', '>').ToLowerInvariant()))
            .ToHashSet(StringComparer.Ordinal);
        var negation = Negation(options.Value.TransactionalKeywords);

        var units = new List<Unit>();
        foreach (var policy in policies)
        {
            var scope = await ScopeAsync(policy, known, ct);
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
                if (Default(policy, scope, negation, settings) is { } unit)
                {
                    units.Add(unit);
                }

                continue;
            }

            // A mixed policy's unmatched mail goes to review, so its default gets no filter.
            foreach (var rule in policy.Rules.Where(r => r.Status == PolicyStatus.Approved).OrderBy(r => r.Position))
            {
                if (RuleUnit(policy, rule, scope, settings) is { } unit)
                {
                    units.Add(unit);
                }
            }
        }

        return [.. Merge(units).OrderByDescending(p => p.MessageCount).ThenBy(p => p.SenderAddress, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The scope's <c>from</c> terms or <c>list:</c> query, and the mail a narrower policy decides, which its filter
    /// excludes. A relay address (hide-my-email) carries the relay's domain in its From header, so
    /// <c>from:@&lt;canonical domain&gt;</c> would not match it: the raw addresses behind the canonical one are the only
    /// criteria that work. A domain's non-relay senders are covered by <c>@domain</c>; a relay address with its own sender
    /// policy is left to that policy's filter. A sender policy outranks every other, so a list filter excludes every
    /// sender with a policy (any of them can post), and a domain filter those under it, every list with a policy and
    /// the subdomains the portal has seen, which <c>from:@domain</c> matches but the domain policy doesn't decide.
    /// </summary>
    private async Task<Scope> ScopeAsync(SenderPolicyRow policy, Known known, CancellationToken ct)
    {
        var key = policy.ScopeKey;
        if (policy.Scope == PolicyScope.List)
        {
            var count = await db.Messages.CountAsync(m => m.ListId != null && m.ListId.ToLower() == key, ct);
            return new Scope([], [], "list:" + ListKey(key), Excluding("from", known.PolicyAddresses(_ => true)), count);
        }

        var raw = policy.Scope == PolicyScope.Sender
            ? known.Senders.Where(s => s.CanonicalAddress == key).ToList()
            : known.Senders.Where(s => s.CanonicalDomain == key && !known.SenderKeys.Contains(s.CanonicalAddress)).ToList();
        List<string> addresses = [.. raw.Select(s => s.Address.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)];
        if (policy.Scope == PolicyScope.Sender)
        {
            List<string> terms = addresses.Count > 0 ? addresses : [key.ToLowerInvariant()];
            return new Scope(terms, terms, null, [], raw.Sum(s => s.TotalCount));
        }

        var domain = "@" + key.ToLowerInvariant();
        var relays = addresses.Where(a => !FilterCriteriaMapping.FromTermMatches(domain, a));
        var subdomains = known.Domains.Where(d => d.EndsWith("." + key, StringComparison.Ordinal)).Select(d => "@" + d.ToLowerInvariant());
        return new Scope(
            [domain, .. relays],
            addresses.Count > 0 ? addresses : [domain],
            null,
            [.. Excluding("from", [.. known.PolicyAddresses(k => k.EndsWith("@" + key, StringComparison.Ordinal)), .. subdomains]), .. Excluding("list", known.ListKeys)],
            raw.Sum(s => s.TotalCount));
    }

    private static List<string> Excluding(string op, IEnumerable<string> values) =>
        [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(v => $"-{op}:{v}")];

    /// <summary>
    /// A non-mixed policy's filter: the topic label, and skip inbox only when nothing in the portal can decide otherwise
    /// for any matching message (<see cref="PolicyLookup.For"/> ranks a sender policy first, <see cref="PolicyMatcher"/>
    /// ignores a single-label policy's rules and <see cref="ActionPlanner"/> leaves the inbox for every archive or
    /// delete outcome except action mail); null when the exclusions don't fit the cap or the filter would do nothing.
    /// </summary>
    private static Unit? Default(SenderPolicyRow policy, Scope scope, string negation, AppSettings settings)
    {
        var needsAction = policy.MailType == MailType.ActionBill;
        var why = policy.Action == PolicyAction.Keep ? "the policy keeps the mail in the inbox"
            : policy.Action is not (PolicyAction.Archive or PolicyAction.Delete) ? "only an archive or delete policy leaves the inbox"
            : needsAction ? "the policy's mail needs action, so it stays in the inbox"
            : policy.Scope != PolicyScope.Sender ? "a narrower policy the portal hasn't seen yet could decide the mail"
            : !settings.PolicyAutoApplyFetched ? "automatic policy apply on fetched mail is off, so the portal sends new mail to review"
            : !FitsAlone(scope.From, [.. scope.Base, .. scope.Narrower, negation]) ? "excluding every transactional keyword would make the filter too long"
            : null;
        var skip = why is null;
        var topic = Topic(policy.TopicLabel, settings);
        if (topic is null && !skip)
        {
            return null;
        }

        List<string> notes = [skip ? SkipsNote : LabelOnlyNote(why!, settings)];
        if (topic is null)
        {
            notes.Add(DeleteLabelNote);
        }

        List<string> query = skip ? [.. scope.Base, .. scope.Narrower, negation] : [.. scope.Base, .. scope.Narrower];
        var action = new FilterActionRequest(topic is null ? [] : [topic], skip, false);
        var pattern = new SenderPatternDto(topic, needsAction, false, 0, 1, 0, null);
        return Unit.Fitting(policy, null, scope, query, action, pattern, false, notes);
    }

    /// <summary>
    /// A mixed policy's rule as a label-only filter, or null when Gmail can test none of its conditions, the filter
    /// doesn't fit the cap or the rule's topic is the delete label.
    /// </summary>
    private static Unit? RuleUnit(SenderPolicyRow policy, SenderPolicyRuleRow rule, Scope scope, AppSettings settings)
    {
        var terms = Criteria(rule.Match);

        // With no testable condition the filter would label the whole scope.
        if (terms.Query.Count == 0 || Topic(rule.TopicLabel, settings) is not { } topic)
        {
            return null;
        }

        var action = new FilterActionRequest([topic], false, false);
        var pattern = new SenderPatternDto(topic, false, false, 0, 1, 0, null);
        return Unit.Fitting(
            policy, rule, scope, [.. scope.Base, .. scope.Narrower, .. terms.Query], action, pattern, terms.Wider, [.. terms.Notes, LabelOnlyNote(MixedNote, settings)]);
    }

    /// <summary>The label a filter may add: the topic label, unless it is empty or the delete label.</summary>
    private static string? Topic(string? label, AppSettings settings) =>
        Set(label) is { } topic && !string.Equals(topic, settings.DeleteLabelName.Trim(), StringComparison.OrdinalIgnoreCase) ? topic : null;

    private static string LabelOnlyNote(string why, AppSettings settings) =>
        $"Labels only: {why}; " + (settings.PolicyAutoApplyFetched ? "the portal applies the policy's action when it fetches the mail" : "new mail goes to review");

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
            u.Query, string.Join('\n', u.Action.AddLabelNames ?? []), u.Action.SkipInbox, u.Pattern.NeedsAction, u.From.Count == 0,
            Own: u.Partial ? (u.Policy.Id, u.Rule?.Id) : default)))
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

    /// <summary>What the portal knows for the exclusions: the policies' senders, keys and every canonical domain seen.</summary>
    private sealed record Known(List<RawSender> Senders, string[] SenderKeys, List<string> ListKeys, List<string> Domains)
    {
        /// <summary>The raw addresses of the sender policies whose key passes <paramref name="where"/>, or the key when none is stored.</summary>
        public IEnumerable<string> PolicyAddresses(Func<string, bool> where) =>
            SenderKeys.Where(where).SelectMany(k => Senders.Where(s => s.CanonicalAddress == k).Select(s => s.Address).DefaultIfEmpty(k))
                .Select(a => a.ToLowerInvariant());
    }

    /// <param name="From">The <c>from</c> terms; empty for a list scope.</param>
    /// <param name="Addresses">What decides whether an active filter covers the scope.</param>
    /// <param name="List">The <c>list:</c> term of a list scope.</param>
    /// <param name="Narrower">Exclusions of the mail a narrower policy decides, or a subdomain's.</param>
    private sealed record Scope(List<string> From, List<string> Addresses, string? List, List<string> Narrower, int MessageCount)
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
