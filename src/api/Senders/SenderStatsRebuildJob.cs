using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Rules;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Senders;

/// <param name="After">The last sender address rebuilt; the next chunk starts after it.</param>
/// <param name="Done">Senders rebuilt so far.</param>
/// <param name="Total">The sender count taken at the start, raised to <paramref name="Done"/> if more rows appear.</param>
public sealed record SenderStatsRebuildCursor(string? After, int Done, int Total, int Human, int Bulk, int Mixed, int Unknown);

/// <summary>
/// "Sender stats rebuild" (#347): recomputes every sender row's engagement counts over its live messages
/// (<c>!deleted_in_gmail</c>) and its <see cref="SenderKind"/>, in address order and chunks of <see cref="ChunkSize"/>.
/// Each chunk is one grouped query plus one <c>UPDATE</c> that commits with its cursor; the counts are recomputed, never
/// incremented, so a replayed chunk writes the same values. Local data only: never reads Gmail.
/// <para>
/// Replied = the message's thread holds a <c>SENT</c> message, or <see cref="MessageRow.ThreadReplied"/> is true. The
/// <c>SENT</c> source needs the user's sent mail in <c>messages</c> (the All Mail phase); after an Inbox-only fetch
/// <c>replied_count</c> under-counts and only <see cref="MessageRow.ThreadReplied"/> (set at mark time) fills the gap.
/// </para>
/// </summary>
public sealed class SenderStatsRebuildJob(AppDbContext db, TimeProvider time) : IJobHandler
{
    public const string JobType = "sender_stats_rebuild";
    public const string Queue = JobQueues.Maintenance;
    public const int ChunkSize = 200;
    public const string ProgressMessage = "Rebuilding sender stats";

    private static readonly string Primary = SnakeCaseEnumConverter<MessageCategory>.ToDb(MessageCategory.Primary);
    private static readonly string Promotions = SnakeCaseEnumConverter<MessageCategory>.ToDb(MessageCategory.Promotions);
    private static readonly string Social = SnakeCaseEnumConverter<MessageCategory>.ToDb(MessageCategory.Social);
    private static readonly string Updates = SnakeCaseEnumConverter<MessageCategory>.ToDb(MessageCategory.Updates);
    private static readonly string Forums = SnakeCaseEnumConverter<MessageCategory>.ToDb(MessageCategory.Forums);

    public string Type => JobType;

    /// <summary>
    /// Queues a rebuild unless one is already queued, running or paused (that one is returned); the fetch jobs call it
    /// after their completion commits, so the rebuild reads the fetched rows.
    /// </summary>
    public static Task<(JobDto Job, bool Created)> EnqueueAsync(IJobService jobs, CancellationToken ct) =>
        jobs.EnqueueAsync(JobType, Queue, null, ct);

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var cursor = ctx.ReadCursor<SenderStatsRebuildCursor>() ?? new SenderStatsRebuildCursor(null, 0, await db.Senders.CountAsync(ct), 0, 0, 0, 0);
        while (true)
        {
            var senders = db.Senders.AsNoTracking();
            if (cursor.After is { } after)
            {
                senders = senders.Where(s => string.Compare(s.Address, after) > 0);
            }

            var addresses = (await senders.OrderBy(s => s.Address).Select(s => s.Address).Take(ChunkSize).ToListAsync(ct)).ToArray();
            var stats = await CountAsync(addresses, ct);
            var kinds = Array.ConvertAll(addresses, a => SenderStatsCalculator.Kind(stats[a].Counts));
            var done = cursor.Done + addresses.Length;
            cursor = cursor with
            {
                After = addresses.Length > 0 ? addresses[^1] : cursor.After,
                Done = done,
                Total = Math.Max(cursor.Total, done),
                Human = cursor.Human + kinds.Count(k => k == SenderKind.Human),
                Bulk = cursor.Bulk + kinds.Count(k => k == SenderKind.Bulk),
                Mixed = cursor.Mixed + kinds.Count(k => k == SenderKind.Mixed),
                Unknown = cursor.Unknown + kinds.Count(k => k == SenderKind.Unknown),
            };

            if (addresses.Length < ChunkSize)
            {
                cursor = cursor with { Total = cursor.Done };
                await ctx.CompleteAsync(cursor, ProgressOf(cursor, final: true), t => WriteAsync(addresses, stats, kinds, t), ct);
                return;
            }

            var signal = await ctx.CheckpointAsync(cursor, ProgressOf(cursor, final: false), t => WriteAsync(addresses, stats, kinds, t), ct);
            if (signal != JobSignal.Continue)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One grouped query for the chunk: counts per sender and distinct bulk-header combination, so
    /// <see cref="BulkSignal.Of"/> itself decides which combinations are bulk (headers only: no category, no
    /// <c>List-Unsubscribe</c>, which have their own counts). A sender without live messages gets zero counts.
    /// </summary>
    private async Task<Dictionary<string, SenderStats>> CountAsync(string[] addresses, CancellationToken ct)
    {
        var result = addresses.ToDictionary(a => a, _ => new SenderStats(new SenderCounts(0), null), StringComparer.Ordinal);
        if (addresses.Length == 0)
        {
            return result;
        }

        var groups = await db.Database.SqlQuery<HeaderGroup>(
            $"""
            SELECT m.from_address AS "Address", m.precedence AS "Precedence", m.auto_submitted AS "AutoSubmitted", m.list_id AS "ListId",
                count(*)::int AS "Total",
                count(*) FILTER (WHERE {FilterSpec.Unread} = ANY(m.label_ids))::int AS "Unread",
                count(*) FILTER (WHERE {MessageProtection.StarredLabel} = ANY(m.label_ids))::int AS "Starred",
                count(*) FILTER (WHERE m.thread_replied IS TRUE OR EXISTS (
                    SELECT 1 FROM messages r WHERE r.thread_id = m.thread_id AND {MessageProtection.SentLabel} = ANY(r.label_ids)))::int AS "Replied",
                count(*) FILTER (WHERE m.list_unsubscribe ~ '\S')::int AS "ListUnsubscribe",
                count(*) FILTER (WHERE m.category = {Primary})::int AS "Primary",
                count(*) FILTER (WHERE m.category = {Promotions})::int AS "Promotions",
                count(*) FILTER (WHERE m.category = {Social})::int AS "Social",
                count(*) FILTER (WHERE m.category = {Updates})::int AS "Updates",
                count(*) FILTER (WHERE m.category = {Forums})::int AS "Forums",
                min(m.internal_date) AS "FirstSeen"
            FROM messages m
            WHERE m.from_address = ANY({addresses}) AND NOT m.deleted_in_gmail
            GROUP BY m.from_address, m.precedence, m.auto_submitted, m.list_id
            """).ToListAsync(ct);

        foreach (var g in groups)
        {
            var bulk = BulkSignal.Of(new MessageRow { Precedence = g.Precedence, AutoSubmitted = g.AutoSubmitted, ListId = g.ListId }) == MessageOrigin.Bulk;
            var (c, first) = result[g.Address];
            result[g.Address] = new SenderStats(
                new SenderCounts(
                    c.Total + g.Total,
                    c.Unread + g.Unread,
                    c.Replied + g.Replied,
                    c.Starred + g.Starred,
                    c.ListUnsubscribe + g.ListUnsubscribe,
                    c.BulkHeader + (bulk ? g.Total : 0),
                    c.Primary + g.Primary,
                    c.Promotions + g.Promotions,
                    c.Social + g.Social,
                    c.Updates + g.Updates,
                    c.Forums + g.Forums),
                first is null || g.FirstSeen < first ? g.FirstSeen : first);
        }

        return result;
    }

    /// <summary>Writes the chunk's stats in one statement; <c>first_seen_at</c> is only filled, never moved.</summary>
    private async Task WriteAsync(string[] addresses, Dictionary<string, SenderStats> stats, SenderKind[] kinds, CancellationToken ct)
    {
        if (addresses.Length == 0)
        {
            return;
        }

        var c = Array.ConvertAll(addresses, a => stats[a].Counts);
        var unread = Array.ConvertAll(c, x => x.Unread);
        var replied = Array.ConvertAll(c, x => x.Replied);
        var starred = Array.ConvertAll(c, x => x.Starred);
        var listUnsubscribe = Array.ConvertAll(c, x => x.ListUnsubscribe);
        var bulkHeader = Array.ConvertAll(c, x => x.BulkHeader);
        var primary = Array.ConvertAll(c, x => x.Primary);
        var promotions = Array.ConvertAll(c, x => x.Promotions);
        var social = Array.ConvertAll(c, x => x.Social);
        var updates = Array.ConvertAll(c, x => x.Updates);
        var forums = Array.ConvertAll(c, x => x.Forums);
        var kindNames = Array.ConvertAll(kinds, SnakeCaseEnumConverter<SenderKind>.ToDb);
        var firstSeen = Array.ConvertAll(addresses, a => stats[a].FirstSeen);
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE senders AS s SET unread_count = t.unread, replied_count = t.replied, starred_count = t.starred,
                list_unsubscribe_count = t.list_unsubscribe, bulk_header_count = t.bulk_header, primary_count = t.prim,
                promotions_count = t.promotions, social_count = t.social, updates_count = t.updates, forums_count = t.forums,
                kind = t.kind, first_seen_at = COALESCE(s.first_seen_at, t.first_seen), stats_at = {now}
            FROM unnest({addresses}, {unread}, {replied}, {starred}, {listUnsubscribe}, {bulkHeader}, {primary}, {promotions},
                {social}, {updates}, {forums}, {kindNames}, {firstSeen})
                AS t(a, unread, replied, starred, list_unsubscribe, bulk_header, prim, promotions, social, updates, forums, kind, first_seen)
            WHERE s.address = t.a
            """,
            ct);
    }

    private static JobProgress ProgressOf(SenderStatsRebuildCursor cursor, bool final) => new(
        cursor.Done,
        cursor.Total,
        final
            ? $"Senders: {cursor.Human} human, {cursor.Bulk} bulk, {cursor.Mixed} mixed, {cursor.Unknown} unknown"
            : ProgressMessage);

    private sealed record SenderStats(SenderCounts Counts, DateTimeOffset? FirstSeen);

    private sealed class HeaderGroup
    {
        public string Address { get; init; } = "";
        public string? Precedence { get; init; }
        public string? AutoSubmitted { get; init; }
        public string? ListId { get; init; }
        public int Total { get; init; }
        public int Unread { get; init; }
        public int Starred { get; init; }
        public int Replied { get; init; }
        public int ListUnsubscribe { get; init; }
        public int Primary { get; init; }
        public int Promotions { get; init; }
        public int Social { get; init; }
        public int Updates { get; init; }
        public int Forums { get; init; }
        public DateTimeOffset? FirstSeen { get; init; }
    }
}
