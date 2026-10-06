using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// A unit of LLM work: <see cref="RepresentativeIds"/> go to the model, the other members may get a derived suggestion.
/// <see cref="Members"/> are newest first; <see cref="Individual"/> groups are single messages that are never derived.
/// <see cref="Packed"/> groups are one-off senders sharing one snippet-only prompt (<see cref="SingletonPacker"/>).
/// </summary>
public sealed record MessageGroup(
    string Key,
    string SenderAddress,
    string Display,
    IReadOnlyList<MessageRow> Members,
    IReadOnlyList<string> RepresentativeIds,
    bool Individual,
    bool Packed = false);

public sealed record GroupingSettings(
    AnalysisGroupingMode Mode,
    int RepresentativesPerGroup,
    int MinGroupSize,
    double DerivedConfidencePenalty,
    double ClusterDistance,
    ProtectionSettings Protection)
{
    /// <summary>k is at least <see cref="DerivationRule.MinValidRepresentatives"/>, or no group could ever be derived.</summary>
    public static GroupingSettings From(AppSettings s) => new(
        s.AnalysisGroupingMode,
        Math.Max(s.AnalysisRepresentativesPerGroup, DerivationRule.MinValidRepresentatives),
        s.AnalysisMinGroupSize,
        s.AnalysisDerivedConfidencePenalty,
        s.AnalysisClusterDistance,
        s.Protection);
}

/// <param name="EmbeddingFallback">Auto grouping wanted embedding clusters but kept the deterministic groups (#114).</param>
public sealed record GroupingResult(IReadOnlyList<MessageGroup> Groups, bool EmbeddingFallback);

/// <summary>Partitions a run's candidates into groups (epic #22 option A, refined by <see cref="IGroupRefiner"/>).</summary>
public sealed class AnalysisGrouper(IGroupRefiner refiner)
{
    public const string IndividualKeyPrefix = "msg:";
    public const string ListDisplaySuffix = " (List)";
    public const string NoSubjectDisplay = "(no subject)";

    /// <summary>Groups ordered by their newest member (newest first), then key; deterministic for the same input.</summary>
    public Task<GroupingResult> GroupAsync(
        IReadOnlyList<MessageRow> messages,
        GroupingSettings settings,
        Allowlist allowlist,
        PersonalLabels labels,
        CancellationToken ct) =>
        GroupAsync(messages, settings, allowlist, labels, refine: true, ct);

    /// <param name="refine">False keeps Auto deterministic (the preview never calls a model).</param>
    public async Task<GroupingResult> GroupAsync(
        IReadOnlyList<MessageRow> messages,
        GroupingSettings settings,
        Allowlist allowlist,
        PersonalLabels labels,
        bool refine,
        CancellationToken ct)
    {
        var ordered = Newest(messages);
        if (settings.Mode == AnalysisGroupingMode.Off)
        {
            return new GroupingResult(ordered.Select(Single).ToList(), EmbeddingFallback: false);
        }

        List<(MessageGroup Group, IReadOnlyList<string> Seeds, bool OwnDisplay)> keyed = ordered
            .GroupBy(m => GroupKey.ForGrouping(m, labels), StringComparer.Ordinal)
            .Select(g => (Keyed(g.Key, g.ToList(), labels), (IReadOnlyList<string>)[], false))
            .ToList();
        var result = new List<MessageGroup>();
        var fallback = false;
        if (settings.Mode == AnalysisGroupingMode.Auto && refine)
        {
            var refinement = await refiner.RefineAsync([.. keyed.Select(k => k.Group)], settings, ct);
            fallback = refinement.EmbeddingFallback;
            EnsurePartition(ordered, refinement.Groups);
            var deterministic = keyed.Select(k => k.Group.Key).ToHashSet(StringComparer.Ordinal);
            result.AddRange(refinement.Groups.Where(g => g.Individual).SelectMany(g => g.Members).Select(Single));
            keyed = refinement.Groups
                .Where(g => !g.Individual)
                .Select(g =>
                {
                    var group = Keyed(g.Key, Newest(g.Members), labels);
                    return deterministic.Contains(g.Key)
                        ? (group, g.RepresentativeIds, false)
                        : (group with { Display = g.Display + (GroupKey.IsList(g.Key) ? ListDisplaySuffix : "") + LabelsDisplay(g.Key, labels.Names) }, g.RepresentativeIds, true);
                })
                .ToList();
        }

        foreach (var (keyedGroup, seeds, ownDisplay) in keyed)
        {
            // Protected members are never derived: beyond the k newest they go to the model one by one, so a group of
            // protected mail never becomes one oversized prompt.
            var overflow = keyedGroup.Members
                .Where(m => MessageProtection.IsProtected(m, allowlist, settings.Protection))
                .Skip(settings.RepresentativesPerGroup)
                .ToHashSet();
            result.AddRange(overflow.Select(Single));
            var group = overflow.Count == 0 ? keyedGroup
                : ownDisplay ? keyedGroup with { Members = [.. keyedGroup.Members.Where(m => !overflow.Contains(m))] }
                : Keyed(keyedGroup.Key, keyedGroup.Members.Where(m => !overflow.Contains(m)).ToList(), labels);

            if (group.Members.Count < Math.Max(settings.MinGroupSize, 2))
            {
                result.AddRange(group.Members.Select(Single));
                continue;
            }

            result.Add(group with
            {
                RepresentativeIds = RepresentativePicker.Pick(group, settings.RepresentativesPerGroup, allowlist, settings.Protection, seeds),
            });
        }

        return new GroupingResult(
            result
                .OrderByDescending(g => g.Members[0].InternalDate)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList(),
            fallback);
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

    private static MessageGroup Keyed(string key, IReadOnlyList<MessageRow> members, PersonalLabels labels)
    {
        var newest = members[0];
        var display = Subject(newest) + (GroupKey.IsList(key) ? ListDisplaySuffix : "") + LabelsDisplay(key, labels.Names);
        return new MessageGroup(key, newest.FromAddress, display, members, [], Individual: false);
    }

    /// <summary>
    /// The label set a group key splits by, as <c> [name, name]</c> (an unknown id as itself), so groups of one sender
    /// and subject filed differently are told apart; empty for a key without labels.
    /// </summary>
    public static string LabelsDisplay(string key, IReadOnlyDictionary<string, string> names)
    {
        var ids = GroupKey.LabelIds(key);
        return ids.Count == 0 ? "" : $" [{string.Join(", ", ids.Select(id => names.GetValueOrDefault(id, id)))}]";
    }

    /// <summary>A message analysed on its own.</summary>
    internal static MessageGroup Single(MessageRow m) =>
        new(IndividualKeyPrefix + m.Id, m.FromAddress, Subject(m), [m], [m.Id], Individual: true);

    private static string Subject(MessageRow m) => string.IsNullOrWhiteSpace(m.Subject) ? NoSubjectDisplay : m.Subject;
}
