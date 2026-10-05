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
/// per approved rule plus one for a non-mixed policy's default. Proposals that differ only in their senders are merged
/// into <c>from:(a OR b)</c> within <see cref="FilterCriteriaLimits"/>. A delete outcome is the delete label and skip
/// inbox, never Trash, and excludes mail the transactional guard would spare.
/// </summary>
public sealed partial class PolicyFilterProposalQuery(AppDbContext db, ISettingsStore settingsStore, IOptions<PolicyOptions> options)
{
    /// <summary>Transactional keywords a delete filter excludes; Gmail search has no whole-word list of any length.</summary>
    public const int MaxNegatedKeywords = 8;

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
                units.Add(Build(policy, null, scope, new RuleMatch(), allowlisted, negation, settings));
            }

            foreach (var rule in policy.Rules.Where(r => r.Status == PolicyStatus.Approved).OrderBy(r => r.Position))
            {
                units.Add(Build(policy, rule, scope, rule.Match, allowlisted, negation, settings));
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

    private static Unit Build(
        SenderPolicyRow policy, SenderPolicyRuleRow? rule, Scope scope, RuleMatch match, string? allowlisted, string? negation, AppSettings settings)
    {
        var from = scope.From;
        var query = new List<string>();
        var notes = new List<string>();
        var dropped = new List<string>();
        if (scope.ListId is { } list)
        {
            query.Add("list:" + list);
        }

        if (Set(match.FromAddress) is { } address)
        {
            from = [address.ToLowerInvariant()];
        }
        else if (Set(match.FromSubdomain)?.TrimStart('.', '@') is { } subdomain)
        {
            from = ["@" + subdomain.ToLowerInvariant()];
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
                query.Add($"subject:\"{literal}\"");
                if (literal != template.Trim())
                {
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

        var outcome = rule is null
            ? (Topic: policy.TopicLabel ?? "", Type: policy.DocumentTypeLabel, policy.MailType, policy.Action)
            : (Topic: rule.TopicLabel, Type: rule.DocumentTypeLabel, rule.MailType, rule.Action);
        var delete = outcome.Action == PolicyAction.Delete;
        if (delete && dropped.Count > 0)
        {
            // A dropped condition widens the filter beyond what the rule deletes.
            delete = false;
            notes.Add("Archives instead of marking for deletion because the filter is wider than the rule");
        }
        else if (delete && allowlisted is not null)
        {
            delete = false;
            notes.Add($"Archives instead of marking for deletion ({allowlisted})");
        }

        if (delete && negation is not null)
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
        return new Unit(policy, rule, from, string.Join(' ', query), action, pattern, dropped.Count > 0, notes, scope.MessageCount);
    }

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

    /// <summary><c>-has:attachment -subject:(a OR "b c")</c> from the first keywords, or null without any.</summary>
    private static string? Negation(IEnumerable<string> keywords)
    {
        List<string> words = [.. keywords.Select(Phrase).OfType<string>().Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxNegatedKeywords)
            .Select(k => k.Contains(' ', StringComparison.Ordinal) ? $"\"{k}\"" : k)];
        return words.Count == 0 ? "-has:attachment" : $"-has:attachment -subject:({string.Join(" OR ", words)})";
    }

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
