using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis;

/// <summary>
/// Covers members of a group without a model call (memory, #111). A result holds at most one suggestion per member,
/// stored with <see cref="SuggestionSource.Memory"/>; members it leaves out go to the model one by one. Null (never an
/// empty result) sends the whole group to the model.
/// </summary>
public interface IAnalysisShortCircuit
{
    Task<ShortCircuitResult?> TryAsync(MessageGroup group, AppSettings settings, IReadOnlySet<string> allowlisted, CancellationToken ct);
}

public sealed record ShortCircuitResult(IReadOnlyList<SuggestionOutput> Suggestions);
