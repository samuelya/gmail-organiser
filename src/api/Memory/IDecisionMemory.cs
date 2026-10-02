using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;

namespace GmailOrganiser.Memory;

/// <summary>A consistent approved outcome for a sender (or mailing list); <see cref="Approvals"/> agree with it.</summary>
public sealed record MemoryPattern(string TopicLabel, bool NeedsAction, bool ToBeDeleted, int Approvals, double Agreement);

/// <summary>
/// Past decisions as LLM memory (DESIGN §6.2, §6.3): vectors when an embedding model is configured, exact sender or
/// list matches otherwise.
/// </summary>
public interface IDecisionMemory
{
    /// <summary>
    /// Sets <see cref="DecisionRow.Embedding"/> and <see cref="DecisionRow.EmbeddingModel"/> in one model call; leaves
    /// the rows without a vector when no embedding model is configured or the call fails. Throws only on cancellation.
    /// </summary>
    Task EmbedAsync(IReadOnlyList<DecisionRow> decisions, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="k"/> past decisions similar to <paramref name="messages"/> (one embedding call for all),
    /// filled with the sender's or list's latest decisions; deduplicated by label, flags and outcome, best first.
    /// </summary>
    Task<IReadOnlyList<MemoryHint>> FindSimilarAsync(IReadOnlyList<MessageRow> messages, int k, CancellationToken ct);

    /// <summary>The sender's (or list's) consistent approved outcome, or null.</summary>
    Task<MemoryPattern?> FindSenderPatternAsync(string sender, string? listId, string? subjectTemplate, CancellationToken ct);
}
