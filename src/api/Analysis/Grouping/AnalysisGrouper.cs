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
    public static GroupingSettings From(AppSettings s) => new(
        s.AnalysisGroupingMode,
        s.AnalysisRepresentativesPerGroup,
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
        var ordered = messages.OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        if (settings.Mode == AnalysisGroupingMode.Off)
        {
            return ordered.Select(Single).ToList();
        }

        IReadOnlyList<MessageGroup> keyed = ordered
            .GroupBy(GroupKey.For, StringComparer.Ordinal)
            .Select(g => Keyed(g.Key, g.ToList()))
            .ToList();
        if (settings.Mode == AnalysisGroupingMode.Auto)
        {
            keyed = await refiner.RefineAsync(keyed, settings, ct);
        }

        var result = new List<MessageGroup>();
        foreach (var group in keyed)
        {
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

    private static MessageGroup Keyed(string key, IReadOnlyList<MessageRow> members)
    {
        var newest = members[0];
        var display = Subject(newest) + (GroupKey.IsList(key) ? ListDisplaySuffix : "");
        return new MessageGroup(key, newest.FromAddress, display, members, [], Individual: false);
    }

    private static MessageGroup Single(MessageRow m) =>
        new(IndividualKeyPrefix + m.Id, m.FromAddress, Subject(m), [m], [m.Id], Individual: true);

    private static string Subject(MessageRow m) => string.IsNullOrWhiteSpace(m.Subject) ? NoSubjectDisplay : m.Subject;
}
