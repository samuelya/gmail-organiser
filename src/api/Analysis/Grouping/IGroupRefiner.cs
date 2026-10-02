namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// Refines the keyed groups in <see cref="Settings.AnalysisGroupingMode.Auto"/> mode (embedding clusters, #114) before
/// small groups are split and representatives are picked; implementations leave <c>RepresentativeIds</c> empty.
/// </summary>
public interface IGroupRefiner
{
    Task<IReadOnlyList<MessageGroup>> RefineAsync(IReadOnlyList<MessageGroup> groups, GroupingSettings settings, CancellationToken ct);
}

/// <summary>Keeps the deterministic groups unchanged (no embedding model).</summary>
public sealed class NoOpGroupRefiner : IGroupRefiner
{
    public Task<IReadOnlyList<MessageGroup>> RefineAsync(IReadOnlyList<MessageGroup> groups, GroupingSettings settings, CancellationToken ct) =>
        Task.FromResult(groups);
}
