using System.Globalization;
using System.Text.RegularExpressions;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Rules.Labels;

/// <summary>
/// The deterministic label review (DESIGN §6.5): empty labels, near-duplicate names and flat → nested renames over the
/// account's user labels. At most one item per label; protected labels are never proposed, nor merged into.
/// </summary>
public static partial class LabelPlanBuilder
{
    /// <summary>Normalised names at least this long also match at edit distance 1.</summary>
    public const int FuzzyMinLength = 6;

    private const string Separators = "-_. ";

    /// <param name="labels">The account's labels in catalogue order (oldest first) with their message counts; system labels are ignored.</param>
    /// <param name="protectedNames">Label names (case-insensitive) that are never proposed: the action and delete labels, the Apps Script labels.</param>
    /// <param name="filtersByLabelId">Active filter ids by the label ids their action adds or removes.</param>
    /// <param name="countsAreExact">
    /// False when the counts come from the fetched mail only: a count of 0 then proves nothing, so no label is proposed as empty.
    /// </param>
    public static IReadOnlyList<LabelPlanItem> Build(
        IReadOnlyList<(GmailLabel Label, long Count)> labels,
        IReadOnlyCollection<string> protectedNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filtersByLabelId,
        bool countsAreExact = true)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(protectedNames);
        ArgumentNullException.ThrowIfNull(filtersByLabelId);
        var isProtected = protectedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var user = labels.Where(l => l.Label.Type == GmailLabelType.User)
            .Select((l, i) => new Entry(l.Label, l.Count, i))
            .ToList();
        var candidates = user.Where(e => !isProtected.Contains(e.Label.Name)).ToList();
        var nests = Nests(user, candidates, isProtected);
        var items = new List<LabelPlanItem>();
        var handled = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<string> Filters(GmailLabel label) => filtersByLabelId.GetValueOrDefault(label.Id) ?? [];

        // A parent, existing or one a kept nest proposal points at, keeps its children's place in the tree.
        bool HasChildren(Entry e) =>
            user.Exists(o => o.Label.Name.StartsWith(e.Label.Name + "/", StringComparison.OrdinalIgnoreCase));
        HashSet<string> NestParents(IReadOnlyCollection<string> droppedIds) =>
            nests.Where(n => !droppedIds.Contains(n.Entry.Label.Id)).Select(n => n.Parent).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An empty label that a surviving nest moves under stays; keeping it can make it a parent in turn.
        var empty = countsAreExact ? candidates.Where(e => e.Count == 0 && !HasChildren(e)).ToList() : [];
        int kept;
        do
        {
            var parents = NestParents([.. empty.Select(e => e.Label.Id)]);
            kept = empty.RemoveAll(e => parents.Contains(e.Label.Name));
        }
        while (kept > 0);

        foreach (var e in empty)
        {
            items.Add(Item(LabelPlanItemKind.Empty, e, Filters(e.Label), "No message carries this label."));
            handled.Add(e.Label.Id);
        }

        // A parent is never merged away. Merging only drops nests, so these parents are a superset of the final ones.
        var nestParents = NestParents(handled);
        bool IsParent(Entry e) => HasChildren(e) || nestParents.Contains(e.Label.Name);

        // Most messages first, then the older label: each label merges into the first duplicate ahead of it.
        var ordered = candidates.Where(e => !handled.Contains(e.Label.Id))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Index)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var into = ordered[i];
            if (handled.Contains(into.Label.Id))
            {
                continue;
            }

            foreach (var e in ordered.Skip(i + 1).Where(e => !handled.Contains(e.Label.Id) && !IsParent(e)))
            {
                if (!AreNearDuplicates(e.Label.Name, into.Label.Name))
                {
                    continue;
                }

                var rationale = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The name nearly duplicates \"{into.Label.Name}\" ({into.Count} messages), so its {e.Count} messages move there.");
                items.Add(Item(LabelPlanItemKind.NearDuplicate, e, Filters(e.Label), rationale) with
                {
                    TargetLabelId = into.Label.Id,
                    TargetLabelName = into.Label.Name,
                });
                handled.Add(e.Label.Id);
            }
        }

        // Nests are counted again over the labels still left, so a shared prefix needs two survivors. A parent stays where
        // it is (renaming it would split it from its children) and drops out of the count in turn.
        var remaining = candidates.Where(e => !handled.Contains(e.Label.Id)).ToList();
        List<Nest> final;
        while (true)
        {
            final = Nests(user, remaining, isProtected);
            var parents = final.Select(n => n.Parent).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var blocked = final.Where(n => HasChildren(n.Entry) || parents.Contains(n.Entry.Label.Name)).Select(n => n.Entry).ToList();
            if (blocked.Count == 0)
            {
                break;
            }

            remaining.RemoveAll(blocked.Contains);
        }

        foreach (var n in final)
        {
            items.Add(Item(LabelPlanItemKind.Nest, n.Entry, Filters(n.Entry.Label), n.Rationale) with { ProposedName = n.ProposedName });
            handled.Add(n.Entry.Label.Id);
        }

        return items;
    }

    /// <summary>
    /// Lower-case, every run of <c>-_. </c> one space, each segment trimmed, a trailing <c>s</c> dropped from the last
    /// segment: <c>"Project_Receipts"</c> → <c>"project receipt"</c>.
    /// </summary>
    public static string Normalise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var segments = name.Split('/')
            .Select(s => SeparatorRun().Replace(s.ToLowerInvariant(), " ").Trim())
            .ToArray();
        var last = segments[^1];
        if (last.Length > 1 && last.EndsWith('s'))
        {
            segments[^1] = last[..^1].TrimEnd();
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Equal normalised names (a <c>/</c> matching a separator), or names whose leaves alone differ, both at least <see cref="FuzzyMinLength"/> chars and one
    /// edit apart. Not a typo, so not a match: a digit changed, inserted or dropped (<c>2023</c> and <c>2024</c>,
    /// <c>Sprint 1</c> and <c>Sprint 10</c>), an edit at the first char of a word (<c>Health</c> and <c>Wealth</c>,
    /// <c>Team A</c> and <c>Team B</c>), at a space, or in a word shorter than three chars (<c>Unit 1A</c> and <c>Unit 1B</c>).
    /// </summary>
    public static bool AreNearDuplicates(string a, string b)
    {
        var (na, nb) = (Normalise(a), Normalise(b));
        if (na.Replace('/', ' ') == nb.Replace('/', ' '))
        {
            return true;
        }

        var xs = na.Split('/');
        var ys = nb.Split('/');
        if (xs.Length != ys.Length || !xs.AsSpan(0, xs.Length - 1).SequenceEqual(ys.AsSpan(0, ys.Length - 1)))
        {
            return false;
        }

        var (x, y) = (xs[^1], ys[^1]);
        if (x.Length < FuzzyMinLength || y.Length < FuzzyMinLength || Math.Abs(x.Length - y.Length) > 1)
        {
            return false;
        }

        var (longer, shorter) = x.Length >= y.Length ? (x, y) : (y, x);
        var at = 0;
        while (at < shorter.Length && longer[at] == shorter[at])
        {
            at++;
        }

        var substituted = longer.Length == shorter.Length;
        var wordStart = longer.LastIndexOf(' ', at) + 1;
        var wordEnd = longer.IndexOf(' ', at) is var end and >= 0 ? end : longer.Length;
        if (longer[at] == ' ' || (substituted && shorter[at] == ' ') || at == wordStart || wordEnd - wordStart - (substituted ? 0 : 1) < 3)
        {
            return false;
        }

        if (substituted)
        {
            return !(char.IsAsciiDigit(longer[at]) && char.IsAsciiDigit(shorter[at]))
                && longer.AsSpan(at + 1).SequenceEqual(shorter.AsSpan(at + 1));
        }

        return !char.IsAsciiDigit(longer[at]) && longer.AsSpan(at + 1).SequenceEqual(shorter.AsSpan(at));
    }

    /// <summary>
    /// Top-level <c>X-Y</c> (or <c>X_Y</c>, <c>X Y</c>, <c>X.Y</c>) labels whose prefix is an existing top-level label (the
    /// longest such prefix wins), or whose first-separator prefix at least two such nests share. Never under a protected label.
    /// </summary>
    private static List<Nest> Nests(List<Entry> user, List<Entry> candidates, HashSet<string> isProtected)
    {
        var topLevel = user.Where(e => !e.Label.Name.Contains('/', StringComparison.Ordinal) && !isProtected.Contains(e.Label.Name))
            .GroupBy(e => e.Label.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Label.Name, StringComparer.OrdinalIgnoreCase);
        var existingNames = user.Select(e => e.Label.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flat = candidates.Where(e => !e.Label.Name.Contains('/', StringComparison.Ordinal) && e.Label.Name.IndexOfAny(Separators.ToCharArray()) > 0)
            .ToList();
        var shared = flat.GroupBy(e => FirstPrefix(e.Label.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2 && !isProtected.Contains(g.Key))
            .ToDictionary(g => g.Key, g => g.Key, StringComparer.OrdinalIgnoreCase);

        var nests = new List<Nest>();
        var proposed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in flat)
        {
            var name = e.Label.Name;
            string? parent = null, child = null, rationale = null;
            var byPrefix = false;
            for (var i = name.Length - 1; i > 0 && parent is null; i--)
            {
                if (Separators.Contains(name[i], StringComparison.Ordinal)
                    && topLevel.TryGetValue(name[..i].TrimEnd(Separators.ToCharArray()), out var existing))
                {
                    parent = existing;
                    child = name[(i + 1)..];
                    rationale = $"\"{existing}\" is an existing top-level label, so this label moves under it.";
                }
            }

            if (parent is null && shared.TryGetValue(FirstPrefix(name), out var prefix))
            {
                parent = prefix;
                child = name[(name.IndexOfAny(Separators.ToCharArray()) + 1)..];
                byPrefix = true;
            }

            child = child?.Trim(Separators.ToCharArray());
            if (parent is null || string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent))
            {
                continue;
            }

            var path = $"{parent}/{child}";
            if (LabelPath.IsValid(path) && !LabelPath.IsReserved(path) && !existingNames.Contains(path) && proposed.Add(path))
            {
                nests.Add(new Nest(e, parent, path, rationale ?? "", byPrefix));
            }
        }

        // A shared prefix needs two labels that actually move; the rationale counts those.
        var moving = nests.Where(n => n.ByPrefix).GroupBy(n => n.Parent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return nests.Where(n => !n.ByPrefix || moving[n.Parent] >= 2)
            .Select(n => n.ByPrefix
                ? n with { Rationale = string.Create(CultureInfo.InvariantCulture, $"{moving[n.Parent]} labels share the prefix \"{n.Parent}\", so they move under it.") }
                : n)
            .ToList();
    }

    private static string FirstPrefix(string name) => name[..name.IndexOfAny(Separators.ToCharArray())];

    private static LabelPlanItem Item(LabelPlanItemKind kind, Entry e, IReadOnlyList<string> filters, string rationale) =>
        new(Guid.NewGuid(), kind, e.Label.Id, e.Label.Name, e.Count, null, null, null, filters, rationale, LabelPlanItemStatus.Proposed);

    [GeneratedRegex("[-_. ]+")]
    private static partial Regex SeparatorRun();

    private sealed record Entry(GmailLabel Label, long Count, int Index);

    private sealed record Nest(Entry Entry, string Parent, string ProposedName, string Rationale, bool ByPrefix);
}
