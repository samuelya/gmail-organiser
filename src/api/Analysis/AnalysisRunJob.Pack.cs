using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;

namespace GmailOrganiser.Analysis;

public sealed partial class AnalysisRunJob
{
    /// <summary>Starts a packed email's body, so the model knows it sees the snippet only.</summary>
    public const string SnippetOnlyMarker = "[snippet only; the full body was not fetched]";

    /// <summary>
    /// One snippet-only prompt for a pack of one-off senders (#376): memory covers what it can per member, the rest go
    /// to the model together, without bodies, attachments, a filter or derivation. Answers at or above
    /// <c>AnalysisPackRetryThreshold</c> are stored; the other members (low confidence, no valid answer, or the whole
    /// pack when the output is unusable) go to the model again one by one with the body, like a mixed group's members.
    /// Every answer passes the run's approved label set like any other (#367).
    /// </summary>
    private async Task<GroupOutcome> AnalysePackAsync(RunContext context, MessageGroup group, PreparedGroup ready, CancellationToken ct)
    {
        var remembered = ready.Covered is { } covered
            ? covered.Suggestions.Where(s => !context.LabelSet.IsBlocked(s)).ToList()
            : [];
        var members = group.Members.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var rows = remembered.Select(s => Row(context, members[s.Id], SuggestionSource.Memory, s, null, null, context.Run.Model)).ToList();
        var rest = PackModelBound(context, group, ready.Covered).ToList();
        if (rest.Count < 2)
        {
            // Nothing left to pack: a lone member gets its body like any individual message.
            return new GroupOutcome(rows, [], [.. rest.Select(AnalysisGrouper.Single)], 0, Mixed: false);
        }

        var emails = rest
            .Select(m => new EmailForPrompt(
                m.Id, m.FromAddress, m.FromName, m.Subject, m.InternalDate, m.Category?.ToString(), !string.IsNullOrEmpty(m.ListUnsubscribe),
                m.HasAttachment, $"{SnippetOnlyMarker}\n{m.Snippet}", context.Labels.NamesOf(m)))
            .ToList();
        var answer = await AskModelAsync(
            context,
            emails,
            await memory.FindSimilarAsync(
                rest, ready.Vectors, DecisionMemory.DefaultSimilarCount, context.Run.DocumentTypeParent, context.HintExclusions, ct),
            context.Policies.HintsFor(rest),
            attachments.Render([]),
            ct);

        // The model's own confidence decides, before the new-label cap lowers it in Row.
        var threshold = context.Settings.AnalysisPackRetryThreshold;
        var confident = answer.Outputs.Values.Where(o => o.Confidence >= threshold).ToList();
        var triageModel = context.Settings.TriageModel;
        rows.AddRange(confident.Select(o => Row(
            context, members[o.Id], SuggestionSource.Llm, o, null, null, answer.TriageIds.Contains(o.Id) ? triageModel : context.Run.Model)));
        var stored = confident.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var retry = rest.Where(m => !stored.Contains(m.Id)).Select(AnalysisGrouper.Single).ToList();
        return new GroupOutcome(
            rows, [], retry, answer.Calls, Mixed: false, Usage: answer.Usage, TriageCalls: answer.TriageCalls,
            EscalatedCalls: answer.EscalatedCalls, PackedMessages: rest.Count, PackRetries: retry.Count);
    }

    /// <summary>The members' memory results as one pack result; null when memory covers none of them.</summary>
    private static ShortCircuitResult? PackCovered(IEnumerable<ShortCircuitResult?> members)
    {
        var suggestions = members.OfType<ShortCircuitResult>().SelectMany(r => r.Suggestions).ToList();
        return suggestions.Count == 0 ? null : new ShortCircuitResult(suggestions);
    }

    /// <summary>A pack's members the model sees: those memory does not cover with a label the run may use.</summary>
    private static IEnumerable<MessageRow> PackModelBound(RunContext context, MessageGroup pack, ShortCircuitResult? covered)
    {
        var remembered = (covered?.Suggestions ?? [])
            .Where(s => !context.LabelSet.IsBlocked(s))
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);
        return pack.Members.Where(m => !remembered.Contains(m.Id));
    }
}
