using System.Collections.Concurrent;
using System.Globalization;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Memory;

/// <summary>
/// Keeps one HNSW cosine index per embedding table, sized to the current model's dimension. The <c>embedding</c> column is
/// an untyped <c>vector</c> and HNSW needs a fixed dimension, so the index is on <c>embedding::vector(dim)</c>, partial on
/// <c>vector_dims(embedding) = dim</c> so rows of an earlier model don't fail the cast. The dimension is only known once
/// the owner's embedding model has answered, so this is runtime DDL, not an EF migration. <c>CONCURRENTLY</c> can't run in
/// a transaction block: it runs only when the context has no transaction open. A session advisory lock keeps two API
/// processes on one database from dropping each other's in-progress build. Best effort: a failure is logged, the
/// (table, dimension) is not tried again for <see cref="FailedBuildBackoff"/>, and the similarity query scans meanwhile.
/// </summary>
public sealed partial class EmbeddingIndexMaintainer(AppDbContext db, TimeProvider time, ILogger<EmbeddingIndexMaintainer> logger)
{
    public const string DecisionsTable = "decisions";

    /// <summary>A build that times out leaves an invalid index to rebuild on the next call, so the build gets time to finish.</summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>A failed build (timeout, out of memory) is not retried before this, so it can't stall every embedding pass.</summary>
    public static readonly TimeSpan FailedBuildBackoff = TimeSpan.FromHours(6);

    /// <summary>The tables with an <c>embedding</c> column; the names are spliced into DDL, so only these are accepted.</summary>
    private static readonly HashSet<string> Tables = new(StringComparer.Ordinal) { DecisionsTable };

    /// <summary>When a failed (database, table, dimension) may be tried again; per process, shared by every scope.</summary>
    private static readonly ConcurrentDictionary<(string Database, string Table, int Dimension), DateTimeOffset> FailedUntil = new();

    public static string IndexName(string table, int dimension) =>
        string.Create(CultureInfo.InvariantCulture, $"ix_{table}_embedding_hnsw_{dimension}");

    /// <summary>Ensures the decisions index for the dimension of the newest embedded decision; nothing when none is embedded.</summary>
    public async Task EnsureLatestAsync(CancellationToken ct)
    {
        var dimension = await db.Database.SqlQuery<int>($"""
            SELECT vector_dims(embedding) AS "Value" FROM decisions WHERE embedding IS NOT NULL ORDER BY created_at DESC LIMIT 1
            """).ToListAsync(ct);
        if (dimension.Count == 1)
        {
            await EnsureAsync(DecisionsTable, dimension[0], ct);
        }
    }

    public Task EnsureAsync(int dimension, CancellationToken ct) => EnsureAsync(DecisionsTable, dimension, ct);

    /// <summary>
    /// Creates <see cref="IndexName"/> for <paramref name="dimension"/> unless a valid one exists (an invalid one, left by a
    /// failed or interrupted concurrent build, is rebuilt), then drops the indexes of other dimensions, so a failed build
    /// leaves the old index in place. One catalog query when nothing changes.
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

        var connection = db.Database.GetDbConnection();
        var key = (connection.DataSource + "/" + connection.Database, table, dimension);
        if (FailedUntil.TryGetValue(key, out var until) && time.GetUtcNow() < until)
        {
            return;
        }

        var wanted = IndexName(table, dimension);
        await db.Database.OpenConnectionAsync(ct);
        var locked = false;
        try
        {
            // The lock and every statement below share the opened connection: the lock is held by the session.
            locked = (await db.Database.SqlQuery<bool>($"""
                SELECT pg_try_advisory_lock(hashtext({"embedding_index:" + table})) AS "Value"
                """).ToListAsync(ct)).Single();
            if (!locked)
            {
                LogSkipped(logger, table, "another process is maintaining it");
                return;
            }

            var existing = await db.Database.SqlQuery<IndexState>($"""
                SELECT c.relname AS "Name", i.indisvalid AS "Valid"
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indexrelid
                JOIN pg_class t ON t.oid = i.indrelid
                WHERE t.relname = {table} AND t.relnamespace = to_regnamespace(current_schema())
                  AND c.relname ~ {$"^ix_{table}_embedding_hnsw_[0-9]+$"}
                """).ToListAsync(ct);
            if (existing.Count == 1 && existing[0] is { Valid: true } only && only.Name == wanted)
            {
                return;
            }

            if (!existing.Any(i => i.Name == wanted && i.Valid))
            {
                if (existing.Any(i => i.Name == wanted))
                {
                    await DropAsync(wanted, ct);
                }

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

            foreach (var index in existing.Where(i => i.Name != wanted))
            {
                await DropAsync(index.Name, ct);
            }

            FailedUntil.TryRemove(key, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FailedUntil[key] = time.GetUtcNow() + FailedBuildBackoff;
            LogSkipped(logger, table, ex.GetType().Name);
        }
        finally
        {
            try
            {
                if (locked)
                {
                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock(hashtext({"embedding_index:" + table}))", CancellationToken.None);
                }
            }
            catch (Npgsql.NpgsqlException)
            {
                // A broken session has released its lock already.
            }

            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task DropAsync(string index, CancellationToken ct)
    {
        // The name comes from the catalog and matched the digits-only pattern, so it needs no escaping.
        var drop = "DROP INDEX CONCURRENTLY IF EXISTS \"" + index + "\"";
        await db.Database.ExecuteSqlRawAsync(drop, ct);
        LogDropped(logger, index);
    }

    private sealed record IndexState(string Name, bool Valid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created embedding index {Index}")]
    private static partial void LogCreated(ILogger logger, string index);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped embedding index {Index}")]
    private static partial void LogDropped(ILogger logger, string index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding index on {Table} left as is: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string table, string reason);
}
