using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// A unit of LLM work: <see cref="RepresentativeIds"/> go to the model, the other members may get a derived suggestion.
/// <see cref="Members"/> are newest first; <see cref="Individual"/> groups are single messages that are never derived.
/// </summary>
public sealed record MessageGroup(
    string Key,
    string SenderAddress,
    string Display,
    IReadOnlyList<MessageRow> Members,
    IReadOnlyList<string> RepresentativeIds,
    bool Individual);

public sealed record GroupingSettings(
    AnalysisGroupingMode Mode,
    int RepresentativesPerGroup,
    int MinGroupSize,
    double DerivedConfidencePenalty,
    double ClusterDistance)
{
    /// <summary>k is at least <see cref="DerivationRule.MinValidRepresentatives"/>, or no group could ever be derived.</summary>
    public static GroupingSettings From(AppSettings s) => new(
        s.AnalysisGroupingMode,
        Math.Max(s.AnalysisRepresentativesPerGroup, DerivationRule.MinValidRepresentatives),
        s.AnalysisMinGroupSize,
        s.AnalysisDerivedConfidencePenalty,
        s.AnalysisClusterDistance);
}

/// <summary>Partitions a run's candidates into groups (epic #22 option A, refined by <see cref="IGroupRefiner"/>).</summary>
public sealed class AnalysisGrouper(IGroupRefiner refiner)
{
    public const string IndividualKeyPrefix = "msg:";
    public const string ListDisplaySuffix = " (List)";
    public const string NoSubjectDisplay = "(no subject)";

    /// <summary>Groups ordered by their newest member (newest first), then key; deterministic for the same input.</summary>
    public async Task<IReadOnlyList<MessageGroup>> GroupAsync(
        IReadOnlyList<MessageRow> messages,
        GroupingSettings settings,
        IReadOnlySet<string> allowlistedSenders,
        CancellationToken ct)
    {
        var ordered = Newest(messages);
        if (settings.Mode == AnalysisGroupingMode.Off)
        {
            return ordered.Select(Single).ToList();
        }

        List<MessageGroup> keyed = ordered
            .GroupBy(GroupKey.For, StringComparer.Ordinal)
            .Select(g => Keyed(g.Key, g.ToList()))
            .ToList();
        if (settings.Mode == AnalysisGroupingMode.Auto)
        {
            var refined = await refiner.RefineAsync(keyed, settings, ct);
            EnsurePartition(ordered, refined);
            keyed = refined.Select(g => Keyed(g.Key, Newest(g.Members))).ToList();
        }

        var result = new List<MessageGroup>();
        foreach (var keyedGroup in keyed)
        {
            // Protected members are never derived: beyond the k newest they go to the model one by one, so a group of
            // protected mail never becomes one oversized prompt.
            var overflow = keyedGroup.Members
                .Where(m => MessageProtection.IsProtected(m, allowlistedSenders))
                .Skip(settings.RepresentativesPerGroup)
                .ToHashSet();
            result.AddRange(overflow.Select(Single));
            var group = overflow.Count == 0
                ? keyedGroup
                : Keyed(keyedGroup.Key, keyedGroup.Members.Where(m => !overflow.Contains(m)).ToList());

            if (group.Members.Count < Math.Max(settings.MinGroupSize, 2))
            {
                result.AddRange(group.Members.Select(Single));
                continue;
            }

            result.Add(group with
            {
                RepresentativeIds = RepresentativePicker.Pick(group, settings.RepresentativesPerGroup, allowlistedSenders),
            });
        }

        return result
            .OrderByDescending(g => g.Members[0].InternalDate)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static List<MessageRow> Newest(IEnumerable<MessageRow> members) =>
        members.OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();

    /// <summary>A refiner may only regroup: every input message in exactly one non-empty group.</summary>
    private static void EnsurePartition(IReadOnlyList<MessageRow> input, IReadOnlyList<MessageGroup> refined)
    {
        var ids = refined.SelectMany(g => g.Members).Select(m => m.Id).ToList();
        var unique = ids.ToHashSet(StringComparer.Ordinal);
        if (refined.Any(g => g.Members.Count == 0) || unique.Count != ids.Count || !unique.SetEquals(input.Select(m => m.Id)))
        {
            throw new InvalidOperationException(
                $"The group refiner must partition the {input.Count} candidates exactly; it returned {ids.Count} members ({unique.Count} distinct).");
        }
    }

    private static MessageGroup Keyed(string key, IReadOnlyList<MessageRow> members)
    {
        var newest = members[0];
        var display = Subject(newest) + (GroupKey.IsList(key) ? ListDisplaySuffix : "");
        return new MessageGroup(key, newest.FromAddress, display, members, [], Individual: false);
    }

    /// <summary>A message analysed on its own.</summary>
    internal static MessageGroup Single(MessageRow m) =>
        new(IndividualKeyPrefix + m.Id, m.FromAddress, Subject(m), [m], [m.Id], Individual: true);

    private static string Subject(MessageRow m) => string.IsNullOrWhiteSpace(m.Subject) ? NoSubjectDisplay : m.Subject;
}
