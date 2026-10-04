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
public static class FilterChecks
{
    /// <summary>The most addresses one merged <c>from</c> filter lists.</summary>
    public const int MergeMaxAddresses = 20;

    private static readonly FilterFix NoFix = new(FilterFixKind.None, []);

    /// <param name="recentMatches">Stored messages newer than the stale cutoff the filter matches; null when not evaluable.</param>
    public static IReadOnlyList<FilterFindingDraft> Run(
        IEnumerable<FilterRow> activeFilters, IReadOnlyList<GmailLabel> labels, Func<FilterRow, int?> recentMatches, int staleDays)
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
        findings.AddRange(DeletedLabels(distinct, names));
        findings.AddRange(Overlaps(distinct, names));
        // Gmail's own Trash and Spam are never fetched, so a filter that sends mail there always counts zero.
        foreach (var f in distinct.Where(f => !f.Forwards && !f.HidesMail && recentMatches(f.Row) == 0))
        {
            findings.Add(new(
                FilterFindingKind.NoRecentMatches,
                [f.Row.Id],
                string.Create(CultureInfo.InvariantCulture, $"{Quote([f])} matches no stored message from the last {staleDays} days."),
                new FilterFix(FilterFixKind.Delete, [f.Row.Id])));
        }

        findings.AddRange(Mergeable(distinct, names));
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

    private static IEnumerable<FilterFindingDraft> DeletedLabels(List<Parsed> filters, Dictionary<string, string> names)
    {
        foreach (var f in filters.Where(f => f.Missing.Count > 0 && !f.Forwards))
        {
            var add = f.Action.AddLabelIds.Where(names.ContainsKey).ToList();
            var remove = f.Action.RemoveLabelIds.Where(names.ContainsKey).ToList();
            var missing = string.Join(", ", f.Missing.Select(id => $"'{id}'"));
            var text = $"{Quote([f])} names {(f.Missing.Count == 1 ? "a label" : "labels")} the mailbox no longer has ({missing})";
            yield return add.Count + remove.Count == 0
                ? new(FilterFindingKind.DeletedLabel, [f.Row.Id], $"{text}; nothing else would remain.",
                    new FilterFix(FilterFixKind.Delete, [f.Row.Id]))
                : new(FilterFindingKind.DeletedLabel, [f.Row.Id], $"{text}; re-create it without them.",
                    new FilterFix(FilterFixKind.DropLabel, [f.Row.Id], new(f.Criteria, new(add, remove))));
        }
    }

    private static IEnumerable<FilterFindingDraft> Overlaps(List<Parsed> filters, Dictionary<string, string> names)
    {
        foreach (var group in filters.GroupBy(f => f.Key).Where(g => g.Count() > 1 && !g.Any(f => f.Forwards)))
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

            // Gmail refuses a filter equal to an existing one, so a member that already has the union is kept.
            var keeper = members.FirstOrDefault(f => f.ActionKey == ActionKey(union));
            var text = $"{Quote(members)} have the same criteria and different actions; one filter can {Describe(union, names)}.";
            yield return new(
                FilterFindingKind.Overlap,
                [.. members.Select(f => f.Row.Id)],
                text,
                keeper is null
                    ? new FilterFix(FilterFixKind.MergeActions, [.. members.Select(f => f.Row.Id)], new(members[0].Criteria, union))
                    : new FilterFix(FilterFixKind.MergeActions, [.. members.Where(f => f != keeper).Select(f => f.Row.Id)]));
        }
    }

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
                    [.. chunk.Select(f => f.Row.Id)],
                    $"{Quote(chunk)} all {Describe(chunk[0].Action, names)}; one filter 'from:({criteria.From})' can replace them.",
                    new FilterFix(FilterFixKind.Merge, [.. chunk.Select(f => f.Row.Id)], new(criteria, chunk[0].Action)));
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
        IReadOnlyList<string> Missing)
    {
        public bool Forwards => !string.IsNullOrEmpty(Action.Forward);

        /// <summary>The action sends mail to Trash or Spam, which the mailbox fetch never stores.</summary>
        public bool HidesMail => Action.AddLabelIds.Any(id => id is MailboxFetchJob.TrashLabelId or MailboxFetchJob.SpamLabelId);
    }
}
