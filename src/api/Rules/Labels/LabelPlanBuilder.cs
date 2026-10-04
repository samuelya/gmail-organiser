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
    public static IReadOnlyList<LabelPlanItem> Build(
        IReadOnlyList<(GmailLabel Label, long Count)> labels,
        IReadOnlyCollection<string> protectedNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filtersByLabelId)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(protectedNames);
        ArgumentNullException.ThrowIfNull(filtersByLabelId);
        var isProtected = protectedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var user = labels.Where(l => l.Label.Type == GmailLabelType.User)
            .Select((l, i) => new Entry(l.Label, l.Count, i))
            .ToList();
        var candidates = user.Where(e => !isProtected.Contains(e.Label.Name)).ToList();
        var nests = Nests(user, candidates);
        var items = new List<LabelPlanItem>();
        var handled = new HashSet<string>(StringComparer.Ordinal);

        IReadOnlyList<string> Filters(GmailLabel label) => filtersByLabelId.GetValueOrDefault(label.Id) ?? [];

        // A parent, existing or one a nest proposal points at, keeps its children's place in the tree.
        bool IsParent(Entry e) =>
            user.Exists(o => o.Label.Name.StartsWith(e.Label.Name + "/", StringComparison.OrdinalIgnoreCase))
            || nests.Exists(n => string.Equals(n.Parent, e.Label.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var e in candidates.Where(e => e.Count == 0 && !IsParent(e)))
        {
            items.Add(Item(LabelPlanItemKind.Empty, e, Filters(e.Label), "No message carries this label."));
            handled.Add(e.Label.Id);
        }

        // Most messages first, then the older label: each label merges into the first duplicate ahead of it.
        var ordered = candidates.Where(e => !handled.Contains(e.Label.Id))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Index)
            .ToList();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            var into = ordered[i];
            if (handled.Contains(into.Label.Id))
            {
                continue;
            }

            foreach (var e in ordered.Skip(i + 1).Where(e => !handled.Contains(e.Label.Id) && !targets.Contains(e.Label.Id)))
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
                targets.Add(into.Label.Id);
            }
        }

        // A parent stays where it is; renaming it would split it from its children.
        foreach (var n in nests.Where(n => !handled.Contains(n.Entry.Label.Id) && !IsParent(n.Entry)))
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
    /// Equal normalised names, or names of at least <see cref="FuzzyMinLength"/> chars one edit apart. A digit changed
    /// to another digit is not an edit here: <c>2023</c> and <c>2024</c> are different labels, not a typo.
    /// </summary>
    public static bool AreNearDuplicates(string a, string b)
    {
        var x = Normalise(a);
        var y = Normalise(b);
        if (x == y)
        {
            return true;
        }

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

        if (longer.Length == shorter.Length)
        {
            return !(char.IsAsciiDigit(longer[at]) && char.IsAsciiDigit(shorter[at]))
                && longer.AsSpan(at + 1).SequenceEqual(shorter.AsSpan(at + 1));
        }

        return longer.AsSpan(at + 1).SequenceEqual(shorter.AsSpan(at));
    }

    /// <summary>
    /// Top-level <c>X-Y</c> (or <c>X_Y</c>, <c>X Y</c>, <c>X.Y</c>) labels whose prefix is an existing top-level label (the
    /// longest such prefix wins), or whose first-separator prefix at least two such labels share.
    /// </summary>
    private static List<Nest> Nests(List<Entry> user, List<Entry> candidates)
    {
        var topLevel = user.Where(e => !e.Label.Name.Contains('/', StringComparison.Ordinal))
            .GroupBy(e => e.Label.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Label.Name, StringComparer.OrdinalIgnoreCase);
        var existingNames = user.Select(e => e.Label.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flat = candidates.Where(e => !e.Label.Name.Contains('/', StringComparison.Ordinal) && e.Label.Name.IndexOfAny(Separators.ToCharArray()) > 0)
            .ToList();
        var shared = flat.GroupBy(e => FirstPrefix(e.Label.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2)
            .ToDictionary(g => g.Key, g => (Name: g.Key, Count: g.Count()), StringComparer.OrdinalIgnoreCase);

        var nests = new List<Nest>();
        var proposed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in flat)
        {
            var name = e.Label.Name;
            string? parent = null, child = null, rationale = null;
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

            if (parent is null && shared.TryGetValue(FirstPrefix(name), out var group))
            {
                parent = group.Name;
                child = name[(name.IndexOfAny(Separators.ToCharArray()) + 1)..];
                rationale = string.Create(
                    CultureInfo.InvariantCulture, $"{group.Count} labels share the prefix \"{group.Name}\", so they move under it.");
            }

            child = child?.Trim(Separators.ToCharArray());
            if (parent is null || string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent))
            {
                continue;
            }

            var path = $"{parent}/{child}";
            if (LabelPath.IsValid(path) && !LabelPath.IsReserved(path) && !existingNames.Contains(path) && proposed.Add(path))
            {
                nests.Add(new Nest(e, parent, path, rationale!));
            }
        }

        return nests;
    }

    private static string FirstPrefix(string name) => name[..name.IndexOfAny(Separators.ToCharArray())];

    private static LabelPlanItem Item(LabelPlanItemKind kind, Entry e, IReadOnlyList<string> filters, string rationale) =>
        new(Guid.NewGuid(), kind, e.Label.Id, e.Label.Name, e.Count, null, null, null, filters, rationale, LabelPlanItemStatus.Proposed);

    [GeneratedRegex("[-_. ]+")]
    private static partial Regex SeparatorRun();

    private sealed record Entry(GmailLabel Label, long Count, int Index);

    private sealed record Nest(Entry Entry, string Parent, string ProposedName, string Rationale);
}
