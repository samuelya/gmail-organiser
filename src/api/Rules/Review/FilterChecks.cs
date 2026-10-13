using System.Globalization;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Rules.Review;

/// <summary>A finding before it is stored.</summary>
public sealed record FilterFindingDraft(FilterFindingKind Kind, IReadOnlyList<string> FilterIds, string Description, FilterFix Fix);

/// <summary>
/// The deterministic filter checks (DESIGN §6.5) over the active filters. Criteria compare by <see cref="Normalise"/>,
/// actions as add/remove label-id sets plus the forward address. A filter that forwards mail is only ever reported as a
/// duplicate, without a fix: the app never creates a forwarding filter, so it could not restore a deleted one. The later
/// copies of a duplicate take part in no other check.
/// </summary>
public static partial class FilterChecks
{
    /// <summary>The most addresses one merged <c>from</c> filter lists.</summary>
    public const int MergeMaxAddresses = 20;

    private static readonly FilterFix NoFix = new(FilterFixKind.None, []);

    /// <param name="recentMatches">Stored messages newer than the stale cutoff the filter matches; null when not evaluable.</param>
    /// <param name="policyProposals">The policy proposals (#373), those an active filter equals included.</param>
    public static IReadOnlyList<FilterFindingDraft> Run(
        IEnumerable<FilterRow> activeFilters, IReadOnlyList<GmailLabel> labels, Func<FilterRow, int?> recentMatches, int staleDays,
        IReadOnlyList<FilterProposalDto>? policyProposals = null)
    {
        ArgumentNullException.ThrowIfNull(activeFilters);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(recentMatches);
        var names = labels.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal);
        var filters = activeFilters
            .OrderBy(r => r.FirstSeenAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => Parse(r, names))
            .ToList();
        var findings = new List<FilterFindingDraft>();

        var later = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in filters.GroupBy(f => (f.Key, f.ActionKey)).Where(g => g.Count() > 1))
        {
            var (kept, copies) = (group.First(), group.Skip(1).ToList());
            later.UnionWith(copies.Select(c => c.Row.Id));
            findings.Add(new(
                FilterFindingKind.Duplicate,
                [.. group.Select(f => f.Row.Id)],
                $"{Quote(copies)} {(copies.Count == 1 ? "repeats" : "repeat")} the earlier '{kept.Row.CriteriaSummary}' ({Describe(kept.Action, names)}).",
                kept.Forwards ? NoFix : new FilterFix(FilterFixKind.Delete, [.. copies.Select(c => c.Row.Id)])));
        }

        var distinct = filters.Where(f => !later.Contains(f.Row.Id)).ToList();
        // A split group (#557) is judged as the one filter the user sees; only the overlap check looks at its members.
        var collapsed = Collapsed(distinct);
        findings.AddRange(DeletedLabels(collapsed, names));
        findings.AddRange(Overlaps(distinct, names));
        // Gmail's own Trash and Spam are never fetched, so a filter that sends mail there always counts zero.
        foreach (var f in collapsed.Where(f => !f.Forwards && !f.HidesMail && recentMatches(f.Row) == 0))
        {
            findings.Add(new(
                FilterFindingKind.NoRecentMatches,
                [.. f.Ids],
                string.Create(CultureInfo.InvariantCulture, $"{Quote([f])} matches no stored message from the last {staleDays} days."),
                new FilterFix(FilterFixKind.Delete, [.. f.Ids])));
        }

        findings.AddRange(Mergeable(collapsed, names));
        findings.AddRange(PolicyFindings(collapsed, policyProposals ?? [], labels, names));
        return findings;
    }

    /// <summary>Trimmed, lower-cased text criteria (blank = absent), booleans false when absent, size as-is.</summary>
    public static GmailFilterCriteria Normalise(GmailFilterCriteria c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(
            Text(c.From), Text(c.To), Text(c.Subject), Text(c.Query), Text(c.NegatedQuery),
            c.HasAttachment ?? false, c.ExcludeChats ?? false, c.Size, c.SizeComparison);

        static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The lower-cased terms (see <see cref="FilterCriteriaMapping.FromTerms"/>) of a criteria that is only <c>from</c>;
    /// null otherwise.
    /// </summary>
    public static IReadOnlyList<string>? FromOnlyTerms(GmailFilterCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        return Normalise(criteria with { From = null }) == Normalise(new GmailFilterCriteria()) && !string.IsNullOrWhiteSpace(criteria.From)
            ? FilterCriteriaMapping.FromTerms(criteria.From)
            : null;
    }

    /// <summary>Whether two filters have the same normalised criteria and the same action sets.</summary>
    public static bool SameFilter(GmailFilterCriteria a, GmailFilterAction aAction, GmailFilterCriteria b, GmailFilterAction bAction) =>
        Normalise(a) == Normalise(b) && ActionKey(aAction) == ActionKey(bAction);

    /// <summary>
    /// A filter naming a deleted label is re-created without it. Of a split group (#557) only the members still needed
    /// stay, and a member left with no user label is deleted while others of its group are active and carry all its
    /// other actions, so re-creating it would only overlap them.
    /// </summary>
    private static IEnumerable<FilterFindingDraft> DeletedLabels(List<Parsed> filters, Dictionary<string, string> names)
    {
        foreach (var f in filters.Where(f => f.Missing.Count > 0 && !f.Forwards))
        {
            var rest = new GmailFilterAction(
                [.. f.Action.AddLabelIds.Where(names.ContainsKey)], [.. f.Action.RemoveLabelIds.Where(names.ContainsKey)]);
            var missing = string.Join(", ", f.Missing.Select(id => $"'{id}'"));
            var text = $"{Quote([f])} names {(f.Missing.Count == 1 ? "a label" : "labels")} the mailbox no longer has ({missing})";
            var others = f.Members is null && f.Row.GroupId is { } group
                ? filters.Where(o => o != f && o.Row.GroupId == group).ToList()
                : [];
            var grouped = others.Count > 0 && f.Action.UserLabelIds.Count > 0 && rest.UserLabelIds.Count == 0
                && rest.AddLabelIds.All(id => others.Any(o => o.Action.AddLabelIds.Contains(id)))
                && rest.RemoveLabelIds.All(id => others.Any(o => o.Action.RemoveLabelIds.Contains(id)));
            yield return rest.IsEmpty || grouped
                ? new(FilterFindingKind.DeletedLabel, [.. f.Ids],
                    $"{text}; {(grouped ? "the rest of its group remains" : "nothing else would remain")}.",
                    new FilterFix(FilterFixKind.Delete, [.. f.Ids]))
                : new(FilterFindingKind.DeletedLabel, [.. f.Ids], $"{text}; re-create it without them.",
                    SplitFix(FilterFixKind.DropLabel, [f], f.Criteria, rest) ?? new FilterFix(FilterFixKind.Delete, [.. f.Ids]));
        }
    }

    private static IEnumerable<FilterFindingDraft> Overlaps(List<Parsed> filters, Dictionary<string, string> names)
    {
        // Filters naming a deleted label get their DeletedLabels fix first.
        foreach (var group in filters.GroupBy(f => f.Key).Where(g => g.Count() > 1 && !g.Any(f => f.Forwards || f.Missing.Count > 0)))
        {
            var members = group.ToList();
            var union = new GmailFilterAction(
                [.. members.SelectMany(f => f.Action.AddLabelIds).Where(names.ContainsKey).Distinct(StringComparer.Ordinal)],
                [.. members.SelectMany(f => f.Action.RemoveLabelIds).Where(names.ContainsKey).Distinct(StringComparer.Ordinal)]);
            if (union.IsEmpty)
            {
                continue;
            }

            if (union.AddLabelIds.Intersect(union.RemoveLabelIds, StringComparer.Ordinal).ToList() is { Count: > 0 } clash)
            {
                var both = string.Join(", ", clash.Select(id => names.GetValueOrDefault(id, id)));
                yield return new(
                    FilterFindingKind.Overlap,
                    [.. members.Select(f => f.Row.Id)],
                    $"{Quote(members)} have the same criteria and opposite actions on {both}; review them by hand.",
                    NoFix);
                continue;
            }

            // Gmail allows one user label per filter (#557): when every member already is one of the union's filters, they
            // are a split group and there is nothing to merge.
            if (SplitFix(FilterFixKind.MergeActions, members, members[0].Criteria, union) is not { } fix)
            {
                continue;
            }

            var one = union.UserLabelIds.Count > 1 ? "one filter per label" : "one filter";
            yield return new(
                FilterFindingKind.Overlap,
                [.. members.Select(f => f.Row.Id)],
                $"{Quote(members)} have the same criteria and different actions; {one} can {Describe(union, names)}.",
                fix);
        }
    }

    /// <summary>
    /// The fix that replaces <paramref name="members"/> by <paramref name="criteria"/> with <paramref name="action"/>, one
    /// Gmail filter per user label (#557). A member that already is one of those filters is kept, as Gmail refuses a
    /// filter equal to an existing one; the create names the whole action, and applying it creates only the filters still
    /// missing, in one group with the kept ones. Null when nothing would change.
    /// </summary>
    private static FilterFix? SplitFix(
        FilterFixKind kind, IEnumerable<Parsed> members, GmailFilterCriteria criteria, GmailFilterAction action, Guid? policyId = null)
    {
        var rows = members.SelectMany(f => f.Members ?? [f]).ToList();
        var key = Normalise(criteria);
        var parts = action.SplitPerUserLabel().Select(ActionKey).ToHashSet(StringComparer.Ordinal);
        var keepers = rows.Where(f => f.Key == key && parts.Contains(f.ActionKey)).DistinctBy(f => f.ActionKey).ToList();
        var deletes = rows.Except(keepers).ToList();
        var complete = keepers.Count == parts.Count;
        return complete && deletes.Count == 0
            ? null
            : new FilterFix(kind, [.. deletes.Select(f => f.Row.Id)], complete ? null : new(criteria, action), policyId);
    }

    /// <summary>
    /// The filters with a split group's members (#557) as one: same criteria, no forward, and each one of the filters
    /// their union splits into. The checks judge the group's union and missing labels, as they would one filter's.
    /// </summary>
    private static List<Parsed> Collapsed(List<Parsed> filters)
    {
        var result = new List<Parsed>();
        foreach (var group in filters.GroupBy(f => f.Key))
        {
            var members = group.ToList();
            var union = new GmailFilterAction(
                [.. members.SelectMany(f => f.Action.AddLabelIds).Distinct(StringComparer.Ordinal)],
                [.. members.SelectMany(f => f.Action.RemoveLabelIds).Distinct(StringComparer.Ordinal)]);
            if (members.Count > 1 && !members.Any(f => f.Forwards)
                && SplitFix(FilterFixKind.MergeActions, members, members[0].Criteria, union) is null)
            {
                var missing = members.SelectMany(f => f.Missing).Distinct(StringComparer.Ordinal).ToList();
                result.Add(members[0] with { Action = union, ActionKey = ActionKey(union), Missing = missing, Members = members });
            }
            else
            {
                result.AddRange(members);
            }
        }

        return result;
    }

    /// <summary>One-sender filters with the same action, a split group (#557) as one; the merged filter is created as a group.</summary>
    private static IEnumerable<FilterFindingDraft> Mergeable(List<Parsed> filters, Dictionary<string, string> names)
    {
        var singles = filters.Where(f => !f.Forwards && f.Missing.Count == 0 && FromOnlyTerms(f.Criteria) is [_]);
        foreach (var group in singles.GroupBy(f => f.ActionKey))
        {
            foreach (var chunk in group.Chunk(MergeMaxAddresses).Where(c => c.Length > 1))
            {
                var terms = chunk.Select(f => FromOnlyTerms(f.Criteria)![0]).ToList();
                var criteria = new GmailFilterCriteria(From: string.Join(" OR ", terms));
                yield return new(
                    FilterFindingKind.Mergeable,
                    [.. chunk.SelectMany(f => f.Ids)],
                    $"{Quote(chunk)} all {Describe(chunk[0].Action, names)}; one filter 'from:({criteria.From})' can replace them.",
                    new FilterFix(FilterFixKind.Merge, [.. chunk.SelectMany(f => f.Ids)], new(criteria, chunk[0].Action)));
            }
        }
    }

    private static Parsed Parse(FilterRow row, Dictionary<string, string> names)
    {
        var criteria = row.ReadCriteria();
        var action = row.ReadAction();
        var missing = action.AddLabelIds.Concat(action.RemoveLabelIds).Where(id => !names.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        return new Parsed(row, criteria, action, Normalise(criteria), ActionKey(action), missing);
    }

    private static string ActionKey(GmailFilterAction a) => string.Join(
        '\n',
        string.Join(',', a.AddLabelIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
        string.Join(',', a.RemoveLabelIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
        a.Forward?.Trim().ToLowerInvariant() ?? "");

    private static string Quote(IEnumerable<Parsed> filters)
    {
        var quoted = filters.Select(f => $"'{f.Row.CriteriaSummary}'").ToList();
        return quoted.Count == 1 ? $"Filter {quoted[0]}" : $"Filters {string.Join(", ", quoted[..^1])} and {quoted[^1]}";
    }

    private static string Describe(GmailFilterAction a, Dictionary<string, string> names)
    {
        var parts = new List<string>();
        if (a.AddLabelIds.Count > 0)
        {
            parts.Add($"add {string.Join(", ", a.AddLabelIds.Select(id => names.GetValueOrDefault(id, id)))}");
        }

        if (a.RemoveLabelIds.Count > 0)
        {
            parts.Add($"remove {string.Join(", ", a.RemoveLabelIds.Select(id => names.GetValueOrDefault(id, id)))}");
        }

        if (!string.IsNullOrEmpty(a.Forward))
        {
            parts.Add("forward");
        }

        return string.Join(" and ", parts);
    }

    private sealed record Parsed(
        FilterRow Row, GmailFilterCriteria Criteria, GmailFilterAction Action, GmailFilterCriteria Key, string ActionKey,
        IReadOnlyList<string> Missing, IReadOnlyList<Parsed>? Members = null)
    {
        /// <summary>The filter's id, or every member's of a split group (#557).</summary>
        public IReadOnlyList<string> Ids => Members?.Select(m => m.Row.Id).ToList() ?? [Row.Id];

        public bool Forwards => !string.IsNullOrEmpty(Action.Forward);

        /// <summary>The action sends mail to Trash or Spam, which the mailbox fetch never stores.</summary>
        public bool HidesMail => Action.AddLabelIds.Any(id => id is MailboxFetchJob.TrashLabelId or MailboxFetchJob.SpamLabelId);
    }
}
