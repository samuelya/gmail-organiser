using GmailOrganiser.Fetch;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// Auto grouping's embedding clusters (#114): per sender (and personal label set, which never mixes), members are
/// clustered greedily in memory by cosine distance to running-mean centroids, so differently worded mail of one kind
/// merges and an odd message splits out. Vectors are stored per message and model and reused by later runs. Without a
/// model, or on any embedding failure, the deterministic groups come back unchanged with <c>EmbeddingFallback</c>.
/// </summary>
public sealed partial class EmbeddingGroupRefiner(
    ISettingsStore settingsStore,
    ILlmClientFactory llm,
    MessageEmbeddingStore store,
    ILogger<EmbeddingGroupRefiner> logger) : IGroupRefiner
{
    public const int BatchSize = 32;
    public const string KeyPrefix = "emb:";

    /// <summary>Members farther than this many cluster distances from their centroid are analysed on their own.</summary>
    public const double OutlierFactor = 2.0;

    public async Task<GroupRefinement> RefineAsync(IReadOnlyList<MessageGroup> groups, GroupingSettings settings, CancellationToken ct)
    {
        if (settings.Mode != AnalysisGroupingMode.Auto || groups.Count == 0)
        {
            return new GroupRefinement(groups);
        }

        IReadOnlyDictionary<string, float[]> vectors;
        try
        {
            vectors = await EmbedAsync([.. groups.SelectMany(g => g.Members)], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogFallback(logger, groups.Sum(g => g.Members.Count), ex.GetType().Name);
            return new GroupRefinement(groups, EmbeddingFallback: true);
        }

        var refined = new List<MessageGroup>();
        foreach (var partition in groups.GroupBy(PartitionOf, StringComparer.Ordinal))
        {
            var members = partition.SelectMany(g => g.Members)
                .OrderByDescending(m => m.InternalDate)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToList();
            refined.AddRange(Cluster(partition.First(), members, vectors, settings.ClusterDistance));
        }

        return new GroupRefinement(refined);
    }

    /// <summary>Unit vectors for every member: stored rows of the generator's model, the rest embedded and stored.</summary>
    private async Task<IReadOnlyDictionary<string, float[]>> EmbedAsync(IReadOnlyList<MessageRow> members, CancellationToken ct)
    {
        var configured = (await settingsStore.GetAsync(ct)).EmbeddingModel;
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new LlmNotConfiguredException(ModelKinds.Embedding);
        }

        using var generator = await llm.CreateEmbeddingGeneratorAsync(ct);

        // The generator's own model wins: with LLM_FAKE the vectors are the fake's, not the Settings model's.
        var model = generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId ?? configured;
        var vectors = await store.LoadAsync(model, [.. members.Select(m => m.Id)], ct);
        var missing = members.Where(m => !vectors.ContainsKey(m.Id)).ToList();
        var created = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var batch in missing.Chunk(BatchSize))
        {
            var embeddings = await generator.GenerateAsync(batch.Select(Text), cancellationToken: ct);
            if (embeddings.Count != batch.Length)
            {
                throw new InvalidOperationException("The embedding model returned an unexpected number of vectors.");
            }

            for (var i = 0; i < batch.Length; i++)
            {
                created[batch[i].Id] = embeddings[i].Vector.ToArray();
            }
        }

        var all = vectors.Concat(created).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var dimension = all.Values.First().Length;
        if (dimension == 0 || all.Values.Any(v => v.Length != dimension))
        {
            throw new EmbeddingDimensionMismatchException();
        }

        await store.SaveAsync(model, created, ct);
        return all.ToDictionary(p => p.Key, p => Normalise(p.Value), StringComparer.Ordinal);
    }

    /// <summary>The text a message is embedded as: sender, subject template and snippet.</summary>
    public static string Text(MessageRow m) => $"{m.FromAddress} | {SubjectNormaliser.Template(m.Subject)} | {m.Snippet}";

    private static string PartitionOf(MessageGroup g)
    {
        var labels = GroupKey.LabelIds(g.Key);
        return labels.Count == 0 ? g.SenderAddress : $"{g.SenderAddress}{GroupKey.LabelsPrefix}{string.Join(',', labels)}";
    }

    private static IEnumerable<MessageGroup> Cluster(
        MessageGroup first, IReadOnlyList<MessageRow> members, IReadOnlyDictionary<string, float[]> vectors, double distance)
    {
        var clusters = new List<(List<MessageRow> Members, double[] Sum, float[] Centroid)>();
        foreach (var m in members)
        {
            var v = vectors[m.Id];
            var best = -1;
            var bestDistance = double.MaxValue;
            for (var c = 0; c < clusters.Count; c++)
            {
                var d = CosineDistance(v, clusters[c].Centroid);
                if (d < bestDistance)
                {
                    best = c;
                    bestDistance = d;
                }
            }

            if (best >= 0 && bestDistance <= distance)
            {
                var (list, sum, _) = clusters[best];
                list.Add(m);
                for (var i = 0; i < sum.Length; i++)
                {
                    sum[i] += v[i];
                }

                clusters[best] = (list, sum, Normalise(sum));
            }
            else
            {
                clusters.Add(([m], [.. v.Select(x => (double)x)], v));
            }
        }

        var labels = GroupKey.LabelIds(first.Key);
        var suffix = labels.Count == 0 ? "" : $"{GroupKey.LabelsPrefix}{string.Join(',', labels)}";
        var n = 0;
        foreach (var (list, _, centroid) in clusters)
        {
            var scored = list.Select(m => (Member: m, Distance: CosineDistance(vectors[m.Id], centroid))).ToList();
            foreach (var outlier in scored.Where(s => s.Distance > OutlierFactor * distance))
            {
                yield return AnalysisGrouper.Single(outlier.Member);
            }

            var kept = scored.Where(s => s.Distance <= OutlierFactor * distance).ToList();
            if (kept.Count == 0)
            {
                continue;
            }

            var nearest = kept.OrderBy(s => s.Distance).ThenBy(s => s.Member.Id, StringComparer.Ordinal).First().Member;
            var farthest = kept.OrderByDescending(s => s.Distance).ThenBy(s => s.Member.Id, StringComparer.Ordinal).First().Member;
            var newest = kept[0].Member;
            n++;
            yield return new MessageGroup(
                $"{KeyPrefix}{first.SenderAddress}:{n}{suffix}",
                newest.FromAddress,
                string.IsNullOrWhiteSpace(nearest.Subject) ? AnalysisGrouper.NoSubjectDisplay : nearest.Subject,
                [.. kept.Select(s => s.Member)],
                [.. new[] { nearest.Id, farthest.Id, newest.Id }.Distinct(StringComparer.Ordinal)],
                Individual: false);
        }
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return 1 - dot;
    }

    private static float[] Normalise(float[] v) => Normalise(v.Select(x => (double)x).ToArray());

    private static float[] Normalise(double[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? [.. v.Select(x => (float)x)] : [.. v.Select(x => (float)(x / norm))];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding clusters unavailable for {Count} messages ({Reason}); the run keeps the deterministic groups")]
    private static partial void LogFallback(ILogger logger, int count, string reason);
}

/// <summary>Vectors of different lengths (a model change mid-store or a broken model); clustering them is meaningless.</summary>
public sealed class EmbeddingDimensionMismatchException()
    : InvalidOperationException("The message embeddings have different dimensions.");
