using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using Pgvector;

namespace GmailOrganiser.Memory;

/// <summary>A consistent approved outcome for a group scope; <see cref="Approvals"/> distinct messages agree with it.</summary>
/// <param name="DocumentTypeLabel">The latest approval's document-type label; null is none.</param>
/// <param name="DocumentTypeDecided">The latest approval was decided under the document-type parent asked about.</param>
public sealed record MemoryPattern(
    string TopicLabel, bool NeedsAction, bool ToBeDeleted, int Approvals, double Agreement, string? DocumentTypeLabel, bool DocumentTypeDecided);

/// <summary>Message vectors of one embedding model, by message id.</summary>
public sealed record MessageVectors(string Model, IReadOnlyDictionary<string, Vector> ById);

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

    /// <summary>Whether the configured embedding model embeds a fixed probe text; false without a model or on failure.</summary>
    Task<bool> CanEmbedAsync(CancellationToken ct);

    /// <summary>The messages' vectors in one model call; null without an embedding model or on failure.</summary>
    Task<MessageVectors?> EmbedMessagesAsync(IReadOnlyList<MessageRow> messages, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="k"/> past decisions similar to <paramref name="messages"/> (by their
    /// <paramref name="vectors"/>), filled with the latest decisions of their senders or lists (one query per distinct
    /// sender and list); deduplicated by label, flags and outcome, best first. A hint's document type counts as decided
    /// only when it was decided under <paramref name="documentTypeParent"/>.
    /// </summary>
    Task<IReadOnlyList<MemoryHint>> FindSimilarAsync(
        IReadOnlyList<MessageRow> messages, MessageVectors? vectors, int k, string? documentTypeParent, CancellationToken ct);

    /// <summary>
    /// The consistent approved outcome per scope key (<see cref="DecisionRow.ScopeKey"/>) in one query; keys without
    /// one are absent. The document type is judged under <paramref name="documentTypeParent"/> (null: the feature is off).
    /// </summary>
    Task<IReadOnlyDictionary<string, MemoryPattern>> FindPatternsAsync(
        IReadOnlyCollection<string> scopeKeys, int minApprovals, string? documentTypeParent, CancellationToken ct);
}
