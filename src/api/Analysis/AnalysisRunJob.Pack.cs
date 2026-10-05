using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;

namespace GmailOrganiser.Analysis;

/// <summary>A pack's valid answer below the retry threshold and the model that gave it, kept while the member is asked alone.</summary>
public sealed record PackFallback(SuggestionOutput Output, string? Model);

public sealed partial class AnalysisRunJob
{
    /// <summary>Starts a packed email's body, so the model knows it sees the snippet only.</summary>
    public const string SnippetOnlyMarker = "[snippet only; the full body was not fetched]";

    /// <summary>
    /// Packs the plan's one-off senders after the memory lookup (#376), as the preview does: a single memory covers
    /// with a label the run may use stays individual (its lookup is kept for the loop), so packs hold model-bound mail only.
    /// </summary>
    private async Task<IReadOnlyList<MessageGroup>> PackAsync(
        RunContext context, IReadOnlyList<MessageGroup> groups, Dictionary<MessageGroup, PreparedGroup> prepared, CancellationToken ct)
    {
        if (context.Settings.AnalysisPackSize < 2)
        {
            return groups;
        }

        var singles = groups.Where(g => g.Individual && g.Members.Count == 1).ToList();
        var found = await shortCircuit.TryAsync(singles, MemoryContext(context), ct);
        for (var i = 0; i < singles.Count; i++)
        {
            if (found[i] is { } covered && covered.Suggestions.Any(s => !context.LabelSet.IsBlocked(s)))
            {
                prepared[singles[i]] = new PreparedGroup(covered, null);
            }
        }

        return SingletonPacker.Pack(
            groups, context.Run.Id, context.Settings.AnalysisPackSize, context.Allowlisted, context.Settings.Protection, prepared.ContainsKey);
    }

    /// <summary>
    /// One snippet-only prompt for a pack of one-off senders (#376): memory covers what it can per member (it changed
    /// since packing), the rest go to the model together, without bodies, attachments, a filter or derivation. Answers at
    /// or above <c>AnalysisPackRetryThreshold</c> are stored; the other members (low confidence, no valid answer, or the
    /// whole pack when the output is unusable) go to the model again one by one with the body, like a mixed group's
    /// members, and a low-confidence answer is kept as their fallback. Every answer passes the run's approved label set
    /// like any other (#367).
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
        string? ModelOf(string id) => answer.TriageIds.Contains(id) ? triageModel : context.Run.Model;
        rows.AddRange(confident.Select(o => Row(context, members[o.Id], SuggestionSource.Llm, o, null, null, ModelOf(o.Id))));
        var stored = confident.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var retry = rest.Where(m => !stored.Contains(m.Id)).Select(AnalysisGrouper.Single).ToList();
        var fallbacks = answer.Outputs.Values
            .Where(o => o.Confidence < threshold)
            .ToDictionary(o => o.Id, o => new PackFallback(o, ModelOf(o.Id)), StringComparer.Ordinal);
        return new GroupOutcome(
            rows, [], retry, answer.Calls, Mixed: false, Usage: answer.Usage, TriageCalls: answer.TriageCalls,
            EscalatedCalls: answer.EscalatedCalls, PackedMessages: rest.Count, PackRetries: retry.Count, Fallbacks: fallbacks);
    }

    /// <summary>
    /// A pack member asked alone keeps its low-confidence pack answer when that gives it none (the triage path keeps a
    /// weaker answer the same way); the answer still passes the approved label set in <see cref="Row"/>.
    /// </summary>
    private static GroupOutcome WithPackFallback(
        RunContext context, MessageGroup group, GroupOutcome outcome, IReadOnlyDictionary<string, PackFallback> fallbacks)
    {
        if (!group.Individual || outcome.Suggestions.Count > 0 || outcome.Individual.Count > 0
            || !fallbacks.TryGetValue(group.Members[0].Id, out var fallback))
        {
            return outcome;
        }

        var message = group.Members[0];
        return outcome with
        {
            Suggestions = [Row(context, message, SuggestionSource.Llm, fallback.Output, null, null, fallback.Model)],
            FailedIds = [.. outcome.FailedIds.Where(id => id != message.Id)],
        };
    }

    private static ShortCircuitContext MemoryContext(RunContext context) =>
        new(context.Settings, context.Allowlisted, context.LabelIndex, context.Labels, context.Run.DocumentTypeParent);

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
