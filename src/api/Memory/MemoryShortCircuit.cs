using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;

namespace GmailOrganiser.Memory;

/// <summary>
/// Memory as a pre-filter (epic #22, option C): when the group's scope (its <see cref="GroupKey"/>: list or sender and
/// category, plus subject template) has a consistent approved pattern, every non-protected member gets that suggestion
/// without a model call. Protected members always go to the model, so a memory <c>toBeDeleted</c> never lands on
/// protected mail. Suggestions only; the user still reviews them.
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
            if (keys[i] is { } key && patterns.TryGetValue(key, out var pattern))
            {
                results[i] = Cover(groups[i], pattern, context);
            }
        }

        return results;
    }

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
