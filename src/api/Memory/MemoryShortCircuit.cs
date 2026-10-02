using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Memory;

/// <summary>
/// Memory as a pre-filter (epic #22, option C): when the group's sender (or list) has a consistent approved pattern,
/// every non-protected member gets that suggestion without a model call. Protected members always go to the model,
/// so a memory <c>toBeDeleted</c> never lands on protected mail. Suggestions only; the user still reviews them.
/// </summary>
public sealed class MemoryShortCircuit(IDecisionMemory memory) : IAnalysisShortCircuit
{
    public async Task<ShortCircuitResult?> TryAsync(
        MessageGroup group, AppSettings settings, IReadOnlySet<string> allowlisted, CancellationToken ct)
    {
        if (!settings.AnalysisMemoryShortCircuit || group.Members.Count == 0)
        {
            return null;
        }

        var covered = group.Members.Where(m => !MessageProtection.IsProtected(m, allowlisted)).ToList();
        if (covered.Count == 0)
        {
            return null;
        }

        // Members of a group share the list (or sender) and the subject template: the first one stands for all.
        var first = group.Members[0];
        if (await memory.FindSenderPatternAsync(first.FromAddress, first.ListId, SubjectNormaliser.Template(first.Subject), ct)
            is not { } pattern)
        {
            return null;
        }

        var confidence = Math.Clamp(pattern.Agreement - settings.AnalysisDerivedConfidencePenalty, 0, 1);
        var reason = $"Matches {pattern.Approvals} approved decisions for this sender";
        return new ShortCircuitResult([.. covered.Select(m => new SuggestionOutput(
            m.Id, pattern.TopicLabel, IsNewLabel: false, pattern.NeedsAction, pattern.ToBeDeleted, UnsubscribeSuggested: false,
            confidence, reason))]);
    }
}
