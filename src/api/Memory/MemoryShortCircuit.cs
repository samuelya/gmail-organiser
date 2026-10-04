using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;

namespace GmailOrganiser.Memory;

/// <summary>
/// Memory as a pre-filter (epic #22, option C): when the group's scope (its <see cref="GroupKey"/>: list or sender and
/// category, plus subject template) has a consistent approved pattern, every non-protected member gets that suggestion
/// without a model call. Protected members always go to the model, so a memory <c>toBeDeleted</c> never lands on
/// protected mail. A group already filed under another personal label than the memorised one goes to the model too.
/// Suggestions only; the user still reviews them.
/// </summary>
public sealed class MemoryShortCircuit(IDecisionMemory memory) : IAnalysisShortCircuit
{
    public async Task<IReadOnlyList<ShortCircuitResult?>> TryAsync(
        IReadOnlyList<MessageGroup> groups, ShortCircuitContext context, CancellationToken ct)
    {
        var results = new ShortCircuitResult?[groups.Count];
        if (!context.Settings.AnalysisMemoryShortCircuit)
        {
            return results;
        }

        // Members of a group share its key: the first one stands for all.
        var keys = groups.Select(g => g.Members.Count == 0 ? null : GroupKey.For(g.Members[0])).ToList();
        var patterns = await memory.FindPatternsAsync(
            [.. keys.OfType<string>()], context.Settings.AnalysisMemoryMinApprovals, ct);
        for (var i = 0; i < groups.Count; i++)
        {
            if (keys[i] is { } key && patterns.TryGetValue(key, out var pattern) && KeepsLabels(groups[i], pattern, context))
            {
                results[i] = Cover(groups[i], pattern, context);
            }
        }

        return results;
    }

    /// <summary>
    /// Whether every personal label of the group is the memorised topic label (the app's action and delete labels do
    /// not count). An id without a known name (deleted in Gmail since the fetch, or names not loaded) counts as
    /// different: the model sees the group instead.
    /// </summary>
    private static bool KeepsLabels(MessageGroup group, MemoryPattern pattern, ShortCircuitContext context) =>
        group.Members.SelectMany(context.Labels.IdsOf).All(id =>
            context.Labels.Names.TryGetValue(id, out var name)
            && string.Equals(name.Trim(), pattern.TopicLabel.Trim(), StringComparison.OrdinalIgnoreCase));

    private static ShortCircuitResult? Cover(MessageGroup group, MemoryPattern pattern, ShortCircuitContext context)
    {
        var covered = group.Members.Where(m => !MessageProtection.IsProtected(m, context.Allowlisted, context.Settings.Protection)).ToList();
        if (covered.Count == 0)
        {
            return null;
        }

        var label = pattern.TopicLabel.Trim();
        var isNewLabel = !context.LabelTree.Any(l => string.Equals(l.Trim(), label, StringComparison.OrdinalIgnoreCase));
        var confidence = Math.Clamp(pattern.Agreement - context.Settings.AnalysisDerivedConfidencePenalty, 0, 1);
        var reason = $"Matches {pattern.Approvals} approved decisions for this sender";
        return new ShortCircuitResult([.. covered.Select(m => new SuggestionOutput(
            m.Id, pattern.TopicLabel, isNewLabel, pattern.NeedsAction, pattern.ToBeDeleted, UnsubscribeSuggested: false,
            confidence, reason))]);
    }
}
