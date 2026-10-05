using System.Globalization;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Memory;

/// <summary>
/// Keeps one HNSW cosine index per embedding table, sized to the current model's dimension. The <c>embedding</c> column is
/// an untyped <c>vector</c> and HNSW needs a fixed dimension, so the index is on <c>embedding::vector(dim)</c>, partial on
/// <c>vector_dims(embedding) = dim</c> so rows of an earlier model don't fail the cast. The dimension is only known once
/// the owner's embedding model has answered, so this is runtime DDL, not an EF migration. <c>CONCURRENTLY</c> can't run in
/// a transaction block: it runs only when the context has no transaction open. Best effort: a failure is logged and the
/// similarity query falls back to a sequential scan.
/// </summary>
public sealed partial class EmbeddingIndexMaintainer(AppDbContext db, ILogger<EmbeddingIndexMaintainer> logger)
{
    public const string DecisionsTable = "decisions";

    /// <summary>A build that times out leaves an invalid index to rebuild on the next call, so the build gets time to finish.</summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The tables with an <c>embedding</c> column; the names are spliced into DDL, so only these are accepted.</summary>
    private static readonly HashSet<string> Tables = new(StringComparer.Ordinal) { DecisionsTable };

    public static string IndexName(string table, int dimension) =>
        string.Create(CultureInfo.InvariantCulture, $"ix_{table}_embedding_hnsw_{dimension}");

    public Task EnsureAsync(int dimension, CancellationToken ct) => EnsureAsync(DecisionsTable, dimension, ct);

    /// <summary>
    /// Creates <see cref="IndexName"/> for <paramref name="dimension"/> unless a valid one exists (an invalid one, left by a
    /// failed concurrent build, is rebuilt) and drops the indexes of other dimensions. One catalog query when nothing changes.
    /// </summary>
    public async Task EnsureAsync(string table, int dimension, CancellationToken ct)
    {
        if (!Tables.Contains(table))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Not an embedding table.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        if (db.Database.CurrentTransaction is not null)
        {
            LogSkipped(logger, table, "a transaction is open");
            return;
        }

        var wanted = IndexName(table, dimension);
        try
        {
            var existing = await db.Database.SqlQuery<IndexState>($"""
                SELECT c.relname AS "Name", i.indisvalid AS "Valid"
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indexrelid
                JOIN pg_class t ON t.oid = i.indrelid
                WHERE t.relname = {table} AND t.relnamespace = to_regnamespace(current_schema())
                  AND c.relname LIKE {$"ix_{table}_embedding_hnsw_%"}
                """).ToListAsync(ct);
            if (existing.Any(i => i.Name == wanted && i.Valid) && existing.Count == 1)
            {
                return;
            }

            foreach (var index in existing.Where(i => i.Name != wanted || !i.Valid))
            {
                var drop = "DROP INDEX CONCURRENTLY IF EXISTS \"" + index.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                await db.Database.ExecuteSqlRawAsync(drop, ct);
                LogDropped(logger, index.Name);
            }

            if (!existing.Any(i => i.Name == wanted && i.Valid))
            {
                var timeout = db.Database.GetCommandTimeout();
                db.Database.SetCommandTimeout(BuildTimeout);
                try
                {
                    await db.Database.ExecuteSqlRawAsync(string.Create(CultureInfo.InvariantCulture, $"""
                        CREATE INDEX CONCURRENTLY IF NOT EXISTS "{wanted}" ON "{table}"
                        USING hnsw ((embedding::vector({dimension})) vector_cosine_ops)
                        WHERE vector_dims(embedding) = {dimension}
                        """), ct);
                }
                finally
                {
                    db.Database.SetCommandTimeout(timeout);
                }

                LogCreated(logger, wanted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogSkipped(logger, table, ex.GetType().Name);
        }
    }

    private sealed record IndexState(string Name, bool Valid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created embedding index {Index}")]
    private static partial void LogCreated(ILogger logger, string index);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped embedding index {Index}")]
    private static partial void LogDropped(ILogger logger, string index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding index on {Table} left as is: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string table, string reason);
}
