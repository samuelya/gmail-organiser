using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis;

/// <summary>
/// Covers members of groups without a model call (memory, #111). A result holds at most one suggestion per member,
/// stored with <see cref="SuggestionSource.Memory"/>; members it leaves out go to the model one by one. Null (never an
/// empty result) sends the whole group to the model.
/// </summary>
public interface IAnalysisShortCircuit
{
    /// <summary>One result per group, in order, from one lookup for all of them.</summary>
    Task<IReadOnlyList<ShortCircuitResult?>> TryAsync(IReadOnlyList<MessageGroup> groups, ShortCircuitContext context, CancellationToken ct);
}

/// <summary>
/// The caller's settings snapshot, allowlist, Gmail user label names (<see cref="LabelTreeIndex.Empty"/> when only counting) and the
/// person's own labels (<see cref="PersonalLabels.None"/> when Gmail is not reachable).
/// </summary>
public sealed record ShortCircuitContext(
    AppSettings Settings,
    Allowlist Allowlisted,
    LabelTreeIndex LabelTree,
    PersonalLabels Labels);

public sealed record ShortCircuitResult(IReadOnlyList<SuggestionOutput> Suggestions);
