using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace GmailOrganiser.Memory;

/// <summary>
/// Decision memory over <c>decisions</c>. Similarity is an exact cosine scan over the rows of the configured embedding
/// model (the column is untyped, so no vector index); rows of another model are ignored until re-embedded.
/// </summary>
public sealed partial class DecisionMemory(
    AppDbContext db, ILlmClientFactory llm, ISettingsStore settingsStore, ILogger<DecisionMemory> logger) : IDecisionMemory
{
    public const int DefaultSimilarCount = 5;
    public const double MaxDistance = 0.35;
    /// <summary>Similarity of a fallback decision that shares only the mailing list, not the sender.</summary>
    public const double ListMatchSimilarity = 0.9;

    /// <summary>A pattern looks at the latest approvals of a scope only, so an old habit fades out.</summary>
    public const int MaxPatternApprovals = 100;

    /// <summary>The text a decision and a message are embedded from: sender, subject template and snippet.</summary>
    public static string EmbeddingText(string sender, string? subjectTemplate, string? snippet) =>
        $"{sender} | {subjectTemplate ?? ""} | {snippet ?? ""}";

    public async Task EmbedAsync(IReadOnlyList<DecisionRow> decisions, CancellationToken ct)
    {
        if (decisions.Count == 0)
        {
            return;
        }

        var ids = decisions.Where(d => d.MessageId is not null).Select(d => d.MessageId!).Distinct().ToArray();
        var snippets = await db.Messages.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Snippet, StringComparer.Ordinal, ct);
        var texts = decisions
            .Select(d => EmbeddingText(d.SenderAddress, d.SubjectTemplate, d.MessageId is { } id ? snippets.GetValueOrDefault(id) : null))
            .ToList();
        if (await EmbedTextsAsync(texts, ct) is not { } embedded)
        {
            return;
        }

        for (var i = 0; i < decisions.Count; i++)
        {
            decisions[i].Embedding = embedded.Vectors[i];
            decisions[i].EmbeddingModel = embedded.Model;
        }
    }

    public async Task<MessageVectors?> EmbedMessagesAsync(IReadOnlyList<MessageRow> messages, CancellationToken ct)
    {
        var distinct = messages.DistinctBy(m => m.Id, StringComparer.Ordinal).ToList();
        if (distinct.Count == 0
            || await EmbedTextsAsync([.. distinct.Select(m => EmbeddingText(m.FromAddress, SubjectNormaliser.Template(m.Subject), m.Snippet))], ct)
                is not { } embedded)
        {
            return null;
        }

        return new MessageVectors(
            embedded.Model, distinct.Select((m, i) => (m.Id, embedded.Vectors[i])).ToDictionary(x => x.Id, x => x.Item2, StringComparer.Ordinal));
    }

    public async Task<IReadOnlyList<MemoryHint>> FindSimilarAsync(
        IReadOnlyList<MessageRow> messages, MessageVectors? vectors, int k, CancellationToken ct)
    {
        if (messages.Count == 0 || k <= 0)
        {
            return [];
        }

        var hits = new List<(DecisionRow Decision, double Similarity, bool Filled)>();
        foreach (var m in messages)
        {
            if (vectors is not null && vectors.ById.TryGetValue(m.Id, out var vector))
            {
                hits.AddRange((await NearestAsync(vectors.Model, vector, k, ct)).Select(f => (f.Decision, f.Similarity, false)));
            }
        }

        if (hits.DistinctBy(h => Outcome(h.Decision)).Count() < k)
        {
            // Once per distinct sender and list: a group's representatives usually share both.
            foreach (var (sender, listId) in messages.Select(m => (m.FromAddress, GroupKey.NormaliseListId(m.ListId))).Distinct())
            {
                hits.AddRange((await LatestForSenderAsync(sender, listId, k, ct)).Select(f => (f.Decision, f.Similarity, true)));
            }
        }

        // Vector hits first, then the exact-sender fill: a fill's similarity 1 must not outrank a real neighbour.
        return hits
            .OrderBy(h => h.Filled)
            .ThenByDescending(h => h.Similarity)
            .ThenByDescending(h => h.Decision.CreatedAt)
            .DistinctBy(h => Outcome(h.Decision))
            .Take(k)
            .Select(h => new MemoryHint(
                h.Decision.SenderAddress, h.Decision.SubjectTemplate, h.Decision.TopicLabel, h.Decision.NeedsAction,
                h.Decision.ToBeDeleted, SnakeCaseEnumConverter<DecisionOutcome>.ToDb(h.Decision.Outcome), h.Similarity))
            .ToList();
    }

    /// <summary>
    /// Per scope: the latest <see cref="MaxPatternApprovals"/> approvals a person verified (of model or edited
    /// suggestions, never derived or memory ones, so memory cannot reinforce itself), one per message; at least
    /// <paramref name="minApprovals"/> of them, all with the same outcome, and no rejection in the scope since the latest.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MemoryPattern>> FindPatternsAsync(
        IReadOnlyCollection<string> scopeKeys, int minApprovals, CancellationToken ct)
    {
        var keys = scopeKeys.Distinct(StringComparer.Ordinal).ToArray();
        var patterns = new Dictionary<string, MemoryPattern>(StringComparer.Ordinal);
        if (keys.Length == 0)
        {
            return patterns;
        }

        var rows = await db.Decisions.AsNoTracking()
            .Where(d => d.ScopeKey != null && keys.Contains(d.ScopeKey))
            .Where(d => d.Outcome == DecisionOutcome.Rejected || d.Source == SuggestionSource.Llm || d.Edited)
            .Select(d => new { d.Id, ScopeKey = d.ScopeKey!, d.MessageId, d.Outcome, d.TopicLabel, d.NeedsAction, d.ToBeDeleted, d.CreatedAt })
            .ToListAsync(ct);
        foreach (var scope in rows.GroupBy(r => r.ScopeKey, StringComparer.Ordinal))
        {
            var approvals = scope
                .Where(r => r.Outcome == DecisionOutcome.Approved)
                .OrderByDescending(r => r.CreatedAt)
                .DistinctBy(r => r.MessageId ?? r.Id.ToString())
                .Take(MaxPatternApprovals)
                .ToList();
            if (approvals.Count == 0 || approvals.Count < minApprovals)
            {
                continue;
            }

            var latest = approvals[0];
            var consistent = approvals.All(a =>
                a.TopicLabel == latest.TopicLabel && a.NeedsAction == latest.NeedsAction && a.ToBeDeleted == latest.ToBeDeleted);
            if (consistent && !scope.Any(r => r.Outcome == DecisionOutcome.Rejected && r.CreatedAt > latest.CreatedAt))
            {
                patterns[scope.Key] = new MemoryPattern(latest.TopicLabel, latest.NeedsAction, latest.ToBeDeleted, approvals.Count, 1.0);
            }
        }

        return patterns;
    }

    private static (string, bool, bool, DecisionOutcome) Outcome(DecisionRow d) => (d.TopicLabel, d.NeedsAction, d.ToBeDeleted, d.Outcome);

    private sealed record Embedded(string Model, IReadOnlyList<Vector> Vectors);

    /// <summary>One model call for all texts; null without an embedding model or on any failure (logged without content).</summary>
    private async Task<Embedded?> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        try
        {
            var model = (await settingsStore.GetAsync(ct)).EmbeddingModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            using var generator = await llm.CreateEmbeddingGeneratorAsync(ct);
            var embeddings = await generator.GenerateAsync(texts, cancellationToken: ct);
            if (embeddings.Count != texts.Count || embeddings.Any(e => e.Vector.Length == 0))
            {
                LogEmbeddingFailed(logger, texts.Count, "unexpected embedding count or empty vector");
                return null;
            }

            return new Embedded(model, [.. embeddings.Select(e => new Vector(e.Vector))]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogEmbeddingFailed(logger, texts.Count, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Top <paramref name="k"/> decisions of the same model within <see cref="MaxDistance"/>. A vector of another
    /// dimension under the same model name (a re-pulled model) makes the scan fail: logged, treated as no hits.
    /// </summary>
    private async Task<List<(DecisionRow Decision, double Similarity)>> NearestAsync(string model, Vector vector, int k, CancellationToken ct)
    {
        try
        {
            var rows = await db.Decisions.AsNoTracking()
                .Where(d => d.EmbeddingModel == model && d.Embedding != null)
                .Select(d => new { Decision = d, Distance = d.Embedding!.CosineDistance(vector) })
                .Where(x => x.Distance <= MaxDistance)
                .OrderBy(x => x.Distance)
                .Take(k)
                .ToListAsync(ct);
            return [.. rows.Select(r => (r.Decision, 1 - r.Distance))];
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.DataException)
        {
            LogEmbeddingFailed(logger, 1, "vector dimension mismatch");
            return [];
        }
    }

    /// <summary>The latest decisions for the sender (similarity 1) or, failing that, its normalised mailing list.</summary>
    private async Task<List<(DecisionRow Decision, double Similarity)>> LatestForSenderAsync(
        string sender, string? listId, int k, CancellationToken ct)
    {
        var rows = await db.Decisions.AsNoTracking()
            .Where(d => d.SenderAddress == sender || (listId != null && d.ListId == listId))
            .OrderByDescending(d => d.SenderAddress == sender)
            .ThenByDescending(d => d.CreatedAt)
            .Take(k)
            .ToListAsync(ct);
        return [.. rows.Select(d => (d, d.SenderAddress == sender ? 1.0 : ListMatchSimilarity))];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Decision memory left {Count} text(s) without a vector: {Reason}")]
    private static partial void LogEmbeddingFailed(ILogger logger, int count, string reason);
}
