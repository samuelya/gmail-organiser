namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// Refines the keyed groups in <see cref="Settings.AnalysisGroupingMode.Auto"/> mode (embedding clusters, #114) before
/// small groups are split and representatives are picked. A refined group may seed <c>RepresentativeIds</c> (the
/// grouper keeps protected members first and fills to k with <see cref="RepresentativePicker"/>), and an
/// <c>Individual</c> group of one member is analysed on its own. A group under a new key keeps its <c>Display</c>.
/// </summary>
public interface IGroupRefiner
{
    Task<GroupRefinement> RefineAsync(IReadOnlyList<MessageGroup> groups, GroupingSettings settings, CancellationToken ct);
}

/// <param name="EmbeddingFallback">Refinement was wanted but unavailable, so the groups are the unrefined input.</param>
public sealed record GroupRefinement(IReadOnlyList<MessageGroup> Groups, bool EmbeddingFallback = false);

/// <summary>Keeps the deterministic groups unchanged (no embedding model).</summary>
public sealed class NoOpGroupRefiner : IGroupRefiner
{
    public Task<GroupRefinement> RefineAsync(IReadOnlyList<MessageGroup> groups, GroupingSettings settings, CancellationToken ct) =>
        Task.FromResult(new GroupRefinement(groups));
}
