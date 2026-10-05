using System.Globalization;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;

namespace GmailOrganiser.Memory;

/// <summary>
/// Decision memory over <c>decisions</c>. Similarity is a cosine search over the rows of the configured embedding model
/// and the query vector's dimension, served by the HNSW index <see cref="EmbeddingIndexMaintainer"/> keeps for that
/// dimension (a scan until it exists); rows of another model are ignored until re-embedded.
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

    /// <summary>Embedded to tell an unavailable embedder from inputs it rejects.</summary>
    public const string ProbeText = "example.com | probe | ";

    /// <summary>Whether the database's pgvector has <c>hnsw.iterative_scan</c> (0.8+); read once per process.</summary>
    private static bool? iterativeScan;

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

    /// <summary>
    /// The nearest decisions of model <c>{0}</c> to vector <c>{1}</c>, at most <c>{2}</c>, leaving out message IDs <c>{3}</c>
    /// when <paramref name="excluding"/>. Unless <paramref name="exact"/>, the predicate and ORDER BY match the partial
    /// expression index for <paramref name="dimension"/> (<see cref="EmbeddingIndexMaintainer.IndexedExpression"/>), so the
    /// planner can use it; the cast only sees rows of that dimension. The exact form orders by the uncast column, which no index serves.
    /// </summary>
    public static string NearestSql(int dimension, bool excluding, bool exact = false) => string.Create(CultureInfo.InvariantCulture, $$"""
        SELECT * FROM decisions
        WHERE embedding_model = {0} AND vector_dims(embedding) = {{dimension}}{{(excluding ? " AND (message_id IS NULL OR NOT message_id = ANY({3}))" : "")}}
        ORDER BY {{(exact ? "embedding <=> {1}" : Indexed(dimension))}}
        LIMIT {2}
        """);

    private static string Indexed(int dimension)
    {
        var (expression, _, queryCast) = EmbeddingIndexMaintainer.IndexedExpression(dimension);
        return expression + " <=> {1}::" + queryCast;
    }

    public async Task<bool> CanEmbedAsync(CancellationToken ct) => await EmbedTextsAsync([ProbeText], ct) is not null;

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
        IReadOnlyList<MessageRow> messages, MessageVectors? vectors, int k, string? documentTypeParent,
        IReadOnlyCollection<string> excludeMessageIds, CancellationToken ct)
    {
        if (messages.Count == 0 || k <= 0)
        {
            return [];
        }

        var excluded = excludeMessageIds.ToArray();
        var hits = new List<(DecisionRow Decision, double Similarity, bool Filled)>();
        await using var scan = vectors is not null && messages.Any(m => vectors.ById.ContainsKey(m.Id)) ? await StrictOrderScanAsync(ct) : null;
        foreach (var m in messages)
        {
            if (vectors is not null && vectors.ById.TryGetValue(m.Id, out var vector))
            {
                hits.AddRange((await NearestAsync(vectors.Model, vector, k, excluded, ct)).Select(f => (f.Decision, f.Similarity, false)));
            }
        }

        if (hits.DistinctBy(h => Outcome(h.Decision, documentTypeParent)).Count() < k)
        {
            // Once per distinct sender and list: a group's representatives usually share both.
            foreach (var (sender, listId) in messages.Select(m => (m.FromAddress, GroupKey.NormaliseListId(m.ListId))).Distinct())
            {
                hits.AddRange((await LatestForSenderAsync(sender, listId, k, excluded, ct)).Select(f => (f.Decision, f.Similarity, true)));
            }
        }

        // Vector hits first, then the exact-sender fill: a fill's similarity 1 must not outrank a real neighbour.
        return hits
            .OrderBy(h => h.Filled)
            .ThenByDescending(h => h.Similarity)
            .ThenByDescending(h => h.Decision.CreatedAt)
            .DistinctBy(h => Outcome(h.Decision, documentTypeParent))
            .Take(k)
            .Select(h => new MemoryHint(
                h.Decision.SenderAddress, h.Decision.SubjectTemplate, h.Decision.TopicLabel, h.Decision.NeedsAction,
                h.Decision.ToBeDeleted, SnakeCaseEnumConverter<DecisionOutcome>.ToDb(h.Decision.Outcome), h.Similarity,
                h.Decision.DocumentTypeLabel, DecidedUnder(h.Decision.DocumentTypeParent, documentTypeParent), h.Decision.MailType))
            .ToList();
    }

    /// <summary>
    /// Per scope: the latest <see cref="MaxPatternApprovals"/> approvals a person verified (of model or edited
    /// suggestions, never derived, memory or sender-pattern ones, so memory cannot reinforce itself), one per message;
    /// at least <paramref name="minApprovals"/> of them, all with the same outcome, and no rejection in the scope since the latest.
    /// Only approvals decided under <paramref name="documentTypeParent"/> vote on the document-type label (case-insensitive,
    /// null is none), so approvals from before the feature or under an earlier parent never contradict the first one under
    /// this parent. The mail type is the one the approvals that have one agree on (#365), none when they differ.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MemoryPattern>> FindPatternsAsync(
        IReadOnlyCollection<string> scopeKeys, int minApprovals, string? documentTypeParent, CancellationToken ct)
    {
        var keys = scopeKeys.Distinct(StringComparer.Ordinal).ToArray();
        var patterns = new Dictionary<string, MemoryPattern>(StringComparer.Ordinal);
        if (keys.Length == 0)
        {
            return patterns;
        }

        var rows = await db.Decisions.AsNoTracking()
            .Where(d => d.ScopeKey != null && keys.Contains(d.ScopeKey))
            .Where(d => d.Outcome == DecisionOutcome.Rejected
                || (d.Source != SuggestionSource.SenderPattern && (d.Source == SuggestionSource.Llm || d.Edited)))
            .Select(d => new { d.Id, ScopeKey = d.ScopeKey!, d.MessageId, d.Outcome, d.TopicLabel, d.NeedsAction, d.ToBeDeleted, d.DocumentTypeLabel, d.DocumentTypeParent, d.MailType, d.CreatedAt })
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
            var typed = approvals.FirstOrDefault(a => DecidedUnder(a.DocumentTypeParent, documentTypeParent));
            var consistent = approvals.All(a =>
                a.TopicLabel == latest.TopicLabel && a.NeedsAction == latest.NeedsAction && a.ToBeDeleted == latest.ToBeDeleted
                && (!DecidedUnder(a.DocumentTypeParent, documentTypeParent) || SameType(a.DocumentTypeLabel, typed!.DocumentTypeLabel)));
            if (consistent && !scope.Any(r => r.Outcome == DecisionOutcome.Rejected && r.CreatedAt > latest.CreatedAt))
            {
                var mailTypes = approvals.Where(a => a.MailType is not null).Select(a => a.MailType).Distinct().ToList();
                patterns[scope.Key] = new MemoryPattern(
                    latest.TopicLabel, latest.NeedsAction, latest.ToBeDeleted, approvals.Count, 1.0, latest.DocumentTypeLabel,
                    DecidedUnder(latest.DocumentTypeParent, documentTypeParent), mailTypes.Count == 1 ? mailTypes[0] : null);
            }
        }

        return patterns;
    }

    private static bool SameType(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a decision made under <paramref name="decidedParent"/> settles the type under <paramref name="parent"/>.</summary>
    private static bool DecidedUnder(string? decidedParent, string? parent) =>
        parent is not null && string.Equals(decidedParent?.Trim(), parent.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A hint's dedup key: an undecided type renders as unknown whatever its stored label.</summary>
    private static (string, bool, bool, bool, string?, DecisionOutcome) Outcome(DecisionRow d, string? parent)
    {
        var decided = DecidedUnder(d.DocumentTypeParent, parent);
        return (d.TopicLabel, d.NeedsAction, d.ToBeDeleted, decided, decided ? d.DocumentTypeLabel?.Trim().ToUpperInvariant() : null, d.Outcome);
    }

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

            // The generator's own model wins: with LLM_FAKE the vectors are the fake's, not the Settings model's.
            var madeBy = generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId ?? model;
            return new Embedded(madeBy, [.. embeddings.Select(e => new Vector(e.Vector))]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogEmbeddingFailed(logger, texts.Count, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Top <paramref name="k"/> decisions of the same model and dimension within <see cref="MaxDistance"/>. The index is
    /// approximate and filters the model and exclusions after its candidate list: with an iterative scan (pgvector 0.8+) it
    /// keeps searching until k rows pass, and when fewer than k come back anyway the exact scan answers. The distance limit
    /// applies after the LIMIT (same result: the rows come nearest first), and the distance is computed here from the
    /// loaded vector rather than sent and computed twice. A vector pgvector rejects (NaN, infinity) means no hits.
    /// </summary>
    private async Task<List<(DecisionRow Decision, double Similarity)>> NearestAsync(
        string model, Vector vector, int k, string[] excluded, CancellationToken ct)
    {
        if (!vector.ToArray().All(float.IsFinite))
        {
            LogEmbeddingFailed(logger, 1, "query vector is not finite");
            return [];
        }

        object[] parameters = excluded.Length == 0 ? [model, vector, k] : [model, vector, k, excluded];
        var rows = await db.Decisions.FromSqlRaw(NearestSql(vector.Memory.Length, excluded.Length > 0), parameters).AsNoTracking().ToListAsync(ct);
        if (rows.Count < k)
        {
            rows = await db.Decisions.FromSqlRaw(NearestSql(vector.Memory.Length, excluded.Length > 0, exact: true), parameters).AsNoTracking().ToListAsync(ct);
        }

        return [.. rows
            .Select(d => (Decision: d, Distance: CosineDistance(d.Embedding!, vector)))
            .Where(r => r.Distance <= MaxDistance)
            .OrderBy(r => r.Distance)
            .Select(r => (r.Decision, 1 - r.Distance))];
    }

    /// <summary>pgvector's cosine distance; a zero vector has none, so it is never within <see cref="MaxDistance"/>.</summary>
    private static double CosineDistance(Vector a, Vector b)
    {
        var x = a.Memory.Span;
        var y = b.Memory.Span;
        double dot = 0, xx = 0, yy = 0;
        for (var i = 0; i < x.Length; i++)
        {
            dot += x[i] * (double)y[i];
            xx += x[i] * (double)x[i];
            yy += y[i] * (double)y[i];
        }

        return xx == 0 || yy == 0 ? double.PositiveInfinity : 1 - (dot / Math.Sqrt(xx * yy));
    }

    /// <summary>
    /// Lets the HNSW scans of this lookup continue past <c>hnsw.ef_search</c> candidates in exact distance order until
    /// enough rows pass the filters. <c>SET LOCAL</c> needs a transaction: a read-only one opened here (rolled back on
    /// dispose), or the caller's, where the setting lasts until it ends. Null without pgvector 0.8.
    /// </summary>
    private async Task<IAsyncDisposable?> StrictOrderScanAsync(CancellationToken ct)
    {
        if (iterativeScan is null)
        {
            var version = await db.Database.SqlQuery<string>($"SELECT extversion AS \"Value\" FROM pg_extension WHERE extname = 'vector'").ToListAsync(ct);
            iterativeScan = version.Count == 1 && Version.TryParse(version[0], out var v) && v >= new Version(0, 8);
        }

        if (iterativeScan is not true)
        {
            return null;
        }

        var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = strict_order", ct);
        return transaction;
    }

    /// <summary>The latest decisions for the sender (similarity 1) or, failing that, its normalised mailing list.</summary>
    private async Task<List<(DecisionRow Decision, double Similarity)>> LatestForSenderAsync(
        string sender, string? listId, int k, string[] excluded, CancellationToken ct)
    {
        var rows = await WithoutMessages(db.Decisions.AsNoTracking().Where(d => d.SenderAddress == sender || (listId != null && d.ListId == listId)), excluded)
            .OrderByDescending(d => d.SenderAddress == sender)
            .ThenByDescending(d => d.CreatedAt)
            .Take(k)
            .ToListAsync(ct);
        return [.. rows.Select(d => (d, d.SenderAddress == sender ? 1.0 : ListMatchSimilarity))];
    }

    /// <summary>Leaves out decisions about <paramref name="excluded"/>; no predicate when empty, so normal runs keep their plan.</summary>
    private static IQueryable<DecisionRow> WithoutMessages(IQueryable<DecisionRow> decisions, string[] excluded) =>
        excluded.Length == 0 ? decisions : decisions.Where(d => d.MessageId == null || !excluded.Contains(d.MessageId));

    [LoggerMessage(Level = LogLevel.Information, Message = "Decision memory left {Count} text(s) without a vector: {Reason}")]
    private static partial void LogEmbeddingFailed(ILogger logger, int count, string reason);
}
