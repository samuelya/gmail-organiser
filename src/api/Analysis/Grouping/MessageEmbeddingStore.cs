using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>
/// Stored message embeddings (#114), read and written in a context of its own: the run's context may hold counters it
/// saves only with its checkpoint, so saving vectors must never commit them early.
/// </summary>
public sealed class MessageEmbeddingStore(IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>The stored vectors of <paramref name="model"/> for the given messages; other models' rows are ignored.</summary>
    public async Task<Dictionary<string, StoredEmbedding>> LoadAsync(string model, IReadOnlyCollection<string> messageIds, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.MessageEmbeddings.AsNoTracking()
            .Where(r => r.Model == model && messageIds.Contains(r.MessageId))
            .Select(r => new { r.MessageId, r.Dimension, r.Embedding, r.CreatedAt })
            .ToListAsync(ct);
        return rows
            .Select(r => (r.MessageId, Stored: new StoredEmbedding(r.Embedding.ToArray(), r.CreatedAt), r.Dimension))
            .Where(r => r.Stored.Vector.Length == r.Dimension)
            .ToDictionary(r => r.MessageId, r => r.Stored, StringComparer.Ordinal);
    }

    /// <summary>Upserts by message id: a row of another model is overwritten with this model's vector.</summary>
    public async Task SaveAsync(string model, IReadOnlyDictionary<string, float[]> vectors, CancellationToken ct)
    {
        if (vectors.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = vectors.Keys.ToList();
        var existing = await db.MessageEmbeddings.Where(r => ids.Contains(r.MessageId)).ToDictionaryAsync(r => r.MessageId, StringComparer.Ordinal, ct);
        var now = time.GetUtcNow();
        foreach (var (id, vector) in vectors)
        {
            if (!existing.TryGetValue(id, out var row))
            {
                row = new MessageEmbeddingRow { MessageId = id };
                db.MessageEmbeddings.Add(row);
            }

            row.Model = model;
            row.Dimension = vector.Length;
            row.Embedding = new Vector(vector);
            row.CreatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>A stored vector and when it was written: the newest row's dimension is the model's current one.</summary>
public sealed record StoredEmbedding(float[] Vector, DateTimeOffset CreatedAt);
