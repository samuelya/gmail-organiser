using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;

namespace GmailOrganiser.Analysis;

/// <summary>
/// Covers a whole group without a model call (memory, #111). All or nothing: a result holds exactly one suggestion per
/// member, stored with <see cref="SuggestionSource.Memory"/>; null sends the group to the model.
/// </summary>
public interface IAnalysisShortCircuit
{
    Task<ShortCircuitResult?> TryAsync(MessageGroup group, CancellationToken ct);
}

public sealed record ShortCircuitResult(IReadOnlyList<SuggestionOutput> Suggestions);

/// <summary>Never short-circuits; replaced by the memory short-circuit in #111.</summary>
public sealed class NoAnalysisShortCircuit : IAnalysisShortCircuit
{
    public Task<ShortCircuitResult?> TryAsync(MessageGroup group, CancellationToken ct) => Task.FromResult<ShortCircuitResult?>(null);
}
