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
    public const double MinAgreement = 0.8;

    /// <summary>Similarity of a fallback decision that shares only the mailing list, not the sender.</summary>
    public const double ListMatchSimilarity = 0.9;

    /// <summary>A pattern looks at the latest approvals only, so an old habit fades out.</summary>
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

    public async Task<IReadOnlyList<MemoryHint>> FindSimilarAsync(IReadOnlyList<MessageRow> messages, int k, CancellationToken ct)
    {
        if (messages.Count == 0 || k <= 0)
        {
            return [];
        }

        var embedded = await EmbedTextsAsync(
            [.. messages.Select(m => EmbeddingText(m.FromAddress, SubjectNormaliser.Template(m.Subject), m.Snippet))], ct);
        var hits = new List<(DecisionRow Decision, double Similarity, bool Filled)>();
        for (var i = 0; i < messages.Count; i++)
        {
            var found = embedded is null ? [] : await NearestAsync(embedded.Model, embedded.Vectors[i], k, ct);
            hits.AddRange(found.Select(f => (f.Decision, f.Similarity, false)));
            if (found.Count < k)
            {
                var seen = found.Select(f => f.Decision.Id).ToHashSet();
                hits.AddRange((await LatestForSenderAsync(messages[i], k, ct))
                    .Where(f => !seen.Contains(f.Decision.Id))
                    .Take(k - found.Count)
                    .Select(f => (f.Decision, f.Similarity, true)));
            }
        }

        // Vector hits first, then the exact-sender fill: a fill's similarity 1 must not outrank a real neighbour.
        return hits
            .OrderBy(h => h.Filled)
            .ThenByDescending(h => h.Similarity)
            .ThenByDescending(h => h.Decision.CreatedAt)
            .DistinctBy(h => (h.Decision.TopicLabel, h.Decision.NeedsAction, h.Decision.ToBeDeleted, h.Decision.Outcome))
            .Take(k)
            .Select(h => new MemoryHint(
                h.Decision.SenderAddress, h.Decision.SubjectTemplate, h.Decision.TopicLabel, h.Decision.NeedsAction,
                h.Decision.ToBeDeleted, SnakeCaseEnumConverter<DecisionOutcome>.ToDb(h.Decision.Outcome), h.Similarity))
            .ToList();
    }

    public async Task<MemoryPattern?> FindSenderPatternAsync(string sender, string? listId, string? subjectTemplate, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        // The list when there is one (the grouping key does the same), else the sender.
        var scope = string.IsNullOrWhiteSpace(listId)
            ? db.Decisions.AsNoTracking().Where(d => d.SenderAddress == sender)
            : db.Decisions.AsNoTracking().Where(d => d.ListId == listId);
        var approvals = await scope
            .Where(d => d.Outcome == DecisionOutcome.Approved)
            .OrderByDescending(d => d.CreatedAt)
            .Take(MaxPatternApprovals)
            .Select(d => new { d.TopicLabel, d.NeedsAction, d.ToBeDeleted, d.CreatedAt })
            .ToListAsync(ct);
        if (approvals.Count == 0 || approvals.Count < settings.AnalysisMemoryMinApprovals)
        {
            return null;
        }

        var top = approvals
            .GroupBy(a => (a.TopicLabel, a.NeedsAction, a.ToBeDeleted))
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(a => a.CreatedAt))
            .First();
        var agreement = (double)top.Count() / approvals.Count;
        if (agreement < MinAgreement)
        {
            return null;
        }

        var latestApproval = approvals[0].CreatedAt;
        var rejectedSince = await scope.AnyAsync(
            d => d.Outcome == DecisionOutcome.Rejected && d.SubjectTemplate == subjectTemplate && d.CreatedAt > latestApproval, ct);
        return rejectedSince
            ? null
            : new MemoryPattern(top.Key.TopicLabel, top.Key.NeedsAction, top.Key.ToBeDeleted, top.Count(), agreement);
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

    /// <summary>The latest decisions for the message's sender (similarity 1) or, failing that, its mailing list.</summary>
    private async Task<List<(DecisionRow Decision, double Similarity)>> LatestForSenderAsync(MessageRow message, int k, CancellationToken ct)
    {
        var sender = message.FromAddress;
        var listId = string.IsNullOrWhiteSpace(message.ListId) ? null : message.ListId;
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
