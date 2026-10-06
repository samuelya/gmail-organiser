using System.Security.Cryptography;
using System.Text;
using GmailOrganiser.Fetch;
using GmailOrganiser.Llm;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// Auto grouping's embedding clusters (#114): per sender or mailing list (and personal label set, which never mixes),
/// members are clustered greedily in memory by cosine distance to running-mean centroids, so differently worded mail of
/// one kind merges and an odd message splits out. Vectors are stored per message and model and reused by later runs and
/// by memory. Without a model, or on an embedding failure, every partition still lacking a vector keeps its
/// deterministic groups and the result says <c>EmbeddingFallback</c>.
/// </summary>
public sealed partial class EmbeddingGroupRefiner(
    ISettingsStore settingsStore,
    ILlmClientFactory llm,
    MessageEmbeddingStore store,
    ILogger<EmbeddingGroupRefiner> logger) : IGroupRefiner
{
    public const int BatchSize = 32;
    public const string KeyPrefix = "emb:";
    private const int HashLength = 16;

    /// <summary>Members farther than this many cluster distances from their centroid are analysed on their own.</summary>
    public const double OutlierFactor = 2.0;

    public async Task<GroupRefinement> RefineAsync(IReadOnlyList<MessageGroup> groups, GroupingSettings settings, CancellationToken ct)
    {
        // A partition smaller than a group can be stays as it is: clustering it could only split it, so it costs no call.
        var minimum = Math.Max(settings.MinGroupSize, 2);
        var partitions = groups
            .GroupBy(PartitionOf, StringComparer.Ordinal)
            .Select(p => (Groups: p.ToList(), Members: Newest(p.SelectMany(g => g.Members))))
            .ToList();
        var clusterable = partitions.Where(p => p.Members.Count >= minimum).ToList();
        if (clusterable.Count == 0)
        {
            return new GroupRefinement(groups);
        }

        var (vectors, failure) = await EmbedAsync([.. clusterable.SelectMany(p => p.Members)], ct);
        if (failure is not null)
        {
            LogFallback(logger, clusterable.Sum(p => p.Members.Count) - vectors.Count, failure);
        }

        // Only a partition with a vector for every member is clustered; the others keep their deterministic groups.
        var refined = new List<MessageGroup>();
        foreach (var (partition, members) in partitions)
        {
            refined.AddRange(members.Count >= minimum && members.All(m => vectors.ContainsKey(m.Id))
                ? Cluster(partition, members, vectors, settings.ClusterDistance)
                : partition);
        }

        return new GroupRefinement(refined, EmbeddingFallback: failure is not null);
    }

    /// <summary>
    /// Unit vectors for the members: stored rows of the generator's model, the rest embedded and stored batch by batch,
    /// so a later failure or a pause keeps what was embedded. On a failure the vectors so far come back with its reason.
    /// A stored row of another dimension than the model's current one (the first fresh vector's, else the newest row's)
    /// is re-embedded, so a model that changed size under the same name heals instead of failing every run.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, float[]> Vectors, string? Failure)> EmbedAsync(
        IReadOnlyList<MessageRow> members, CancellationToken ct)
    {
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        Dictionary<string, StoredEmbedding> stored = [];
        int? dimension = null;
        string? failure = null;
        try
        {
            var configured = (await settingsStore.GetAsync(ct)).EmbeddingModel;
            if (string.IsNullOrWhiteSpace(configured))
            {
                throw new LlmNotConfiguredException(ModelKinds.Embedding);
            }

            using var generator = await llm.CreateEmbeddingGeneratorAsync(ct);
            var model = DecisionMemory.ModelOf(generator, configured);
            stored = await store.LoadAsync(model, [.. members.Select(m => m.Id)], ct);
            await EmbedBatchesAsync([.. members.Where(m => !stored.ContainsKey(m.Id))]);
            dimension ??= NewestDimension(stored);
            await EmbedBatchesAsync([.. members.Where(m => stored.TryGetValue(m.Id, out var s) && s.Vector.Length != dimension)]);

            async Task EmbedBatchesAsync(IReadOnlyList<MessageRow> missing)
            {
                foreach (var batch in missing.Chunk(BatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    var created = await DecisionMemory.GenerateAsync(generator, [.. batch.Select(Text)], ct);
                    dimension ??= created[0].Length;
                    if (created.Any(v => v.Length != dimension))
                    {
                        throw new EmbeddingDimensionMismatchException();
                    }

                    await store.SaveAsync(model, batch.Zip(created).ToDictionary(p => p.First.Id, p => p.Second, StringComparer.Ordinal), ct);
                    foreach (var (m, v) in batch.Zip(created))
                    {
                        vectors[m.Id] = Normalise(v);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failure = ex.GetType().Name;
        }

        // Stored rows of the current dimension count on a failure too: a partition embedded earlier still clusters.
        dimension ??= NewestDimension(stored);
        foreach (var (id, row) in stored.Where(p => p.Value.Vector.Length == dimension))
        {
            vectors.TryAdd(id, Normalise(row.Vector));
        }

        return (vectors, failure);

        static int? NewestDimension(Dictionary<string, StoredEmbedding> rows) => rows.Values.MaxBy(r => r.CreatedAt)?.Vector.Length;
    }

    /// <summary>The text a message is embedded as: sender, subject template and snippet (memory's own text).</summary>
    public static string Text(MessageRow m) => DecisionMemory.EmbeddingText(m);

    /// <summary>
    /// What a cluster stays within: a mailing list (its groups may have several senders) or a sender, plus the personal
    /// label set, which never mixes. It starts every cluster key, so a list cluster keeps its list identity.
    /// </summary>
    private static string PartitionOf(MessageGroup g)
    {
        var labels = GroupKey.LabelIds(g.Key);
        var identity = GroupKey.IsList(g.Key) && GroupKey.NormaliseListId(g.Members[0].ListId) is { } listId
            ? GroupKey.ListPrefix + listId
            : g.SenderAddress;
        return labels.Count == 0 ? identity : $"{identity}{GroupKey.LabelsPrefix}{string.Join(',', labels)}";
    }

    private static List<MessageRow> Newest(IEnumerable<MessageRow> members) =>
        [.. members.OrderByDescending(m => m.InternalDate).ThenBy(m => m.Id, StringComparer.Ordinal)];

    private static IEnumerable<MessageGroup> Cluster(
        IReadOnlyList<MessageGroup> partition, IReadOnlyList<MessageRow> members, IReadOnlyDictionary<string, float[]> vectors, double distance)
    {
        var deterministicKey = partition.SelectMany(g => g.Members.Select(m => (m.Id, g.Key))).ToDictionary(p => p.Id, p => p.Key, StringComparer.Ordinal);
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

        var identity = PartitionOf(partition[0]);
        var labelsAt = identity.IndexOf(GroupKey.LabelsPrefix, StringComparison.Ordinal);
        var (prefix, suffix) = labelsAt < 0 ? (identity, "") : (identity[..labelsAt], identity[labelsAt..]);
        var used = new HashSet<string>(StringComparer.Ordinal);
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
            // Review treats (sender, key) as one group across runs, so the key names what the cluster is made of (its
            // deterministic groups), never its position: another run's cluster of other mail never shares it. Two
            // clusters of the same groups in one run are told apart by the member nearest the centroid.
            var hash = Hash(kept.Select(s => deterministicKey[s.Member.Id]));
            var key = $"{KeyPrefix}{prefix}:{hash}{suffix}";
            if (!used.Add(key))
            {
                key = $"{KeyPrefix}{prefix}:{hash}:{nearest.Id}{suffix}";
                used.Add(key);
            }

            yield return new MessageGroup(
                key,
                newest.FromAddress,
                string.IsNullOrWhiteSpace(nearest.Subject) ? AnalysisGrouper.NoSubjectDisplay : nearest.Subject,
                [.. kept.Select(s => s.Member)],
                [.. new[] { nearest.Id, farthest.Id, newest.Id }.Distinct(StringComparer.Ordinal)],
                Individual: false);
        }
    }

    private static double CosineDistance(float[] a, float[] b) => DecisionMemory.CosineDistance(a, b);

    /// <summary>A short digest of the distinct deterministic keys, in ordinal order.</summary>
    private static string Hash(IEnumerable<string> keys) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))))[..HashLength];

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
