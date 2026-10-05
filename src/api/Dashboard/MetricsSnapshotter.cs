using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Rules;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Dashboard;

/// <summary>
/// Takes and reads triage metric snapshots (DESIGN §8.2). Each count is one aggregate query; nothing loops over messages.
/// </summary>
public sealed class MetricsSnapshotter(AppDbContext db, TimeProvider time)
{
    /// <summary>A scheduled snapshot is skipped while the newest one is younger than this.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(15);

    /// <summary>With no completed job to report, a snapshot is due once the newest one is this old.</summary>
    public static readonly TimeSpan HourlyInterval = TimeSpan.FromHours(1);

    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);
    public const int HistoryDays = 90;

    /// <summary>A completed job of these types makes a snapshot due: they change the counts.</summary>
    public static readonly string[] TriggerJobTypes =
        [MailboxFetchJob.JobType, IncrementalFetchJob.JobType, AnalysisRunJob.JobType, PolicyApplyJob.JobType];

    /// <summary>
    /// Takes a snapshot when one is due: none yet, the newest is an hour old, or a <see cref="TriggerJobTypes"/> job
    /// completed after it; never within <see cref="MinInterval"/> of the newest. A job that completes inside that window is
    /// picked up by the first call after it, since the trigger is read from <c>jobs</c>.
    /// </summary>
    /// <returns>The new snapshot, or null when none was due.</returns>
    public async Task<MetricSnapshotRow?> TakeIfDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var latest = await LatestAsync(ct);
        if (latest is { } taken
            && (now - taken.TakenAt < MinInterval
                || (now - taken.TakenAt < HourlyInterval
                    && !await db.Jobs.AnyAsync(j => TriggerJobTypes.Contains(j.Type) && j.Status == JobStatus.Completed
                        && j.FinishedAt > taken.TakenAt, ct))))
        {
            return null;
        }

        return await TakeAsync(ct);
    }

    /// <summary>Computes and stores a snapshot now, and prunes snapshots older than <see cref="Retention"/>.</summary>
    public async Task<MetricSnapshotRow> TakeAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var live = db.Messages.AsNoTracking().Where(m => !m.DeletedInGmail);
        var llm = await db.AnalysisRuns.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new { Milliseconds = g.Sum(r => r.LlmMilliseconds), PromptTokens = g.Sum(r => r.PromptTokens) })
            .FirstOrDefaultAsync(ct);
        var row = new MetricSnapshotRow
        {
            Id = Guid.CreateVersion7(now),
            TakenAt = now,
            MessagesTotal = await live.CountAsync(ct),
            InboxCount = await live.CountAsync(m => m.LabelIds.Contains(FilterSpec.Inbox), ct),
            InboxUnreadCount = await live.CountAsync(m => m.LabelIds.Contains(FilterSpec.Inbox) && m.LabelIds.Contains(FilterSpec.Unread), ct),
            CoveredByPolicy = await CoveredByPolicy(live).CountAsync(ct),
            CoveredByFilter = await CoveredByFilterAsync(live, ct),
            Analysed = await live.CountAsync(m => m.AnalysisStatus != AnalysisStatus.NotAnalysed, ct),
            Applied = await live.CountAsync(m => m.AnalysisStatus == AnalysisStatus.Applied, ct),
            ToBeDeleted = await live.CountAsync(m => db.Suggestions.Any(s => s.MessageId == m.Id
                && s.Status == SuggestionStatus.Applied && s.ToBeDeleted), ct),
            LlmMillisecondsTotal = llm?.Milliseconds ?? 0,
            PromptTokensTotal = llm?.PromptTokens ?? 0,
        };
        db.MetricSnapshots.Add(row);
        await db.SaveChangesAsync(ct);
        var cutoff = now - Retention;
        await db.MetricSnapshots.Where(r => r.TakenAt < cutoff).ExecuteDeleteAsync(ct);
        return row;
    }

    /// <summary>The newest snapshot and the last snapshot of each UTC day over the last <see cref="HistoryDays"/> days, oldest first.</summary>
    public async Task<TriageMetricsDto> GetAsync(CancellationToken ct)
    {
        var latest = await LatestAsync(ct);
        var since = new DateTimeOffset(time.GetUtcNow().UtcDateTime.Date.AddDays(1 - HistoryDays), TimeSpan.Zero);
        var rows = await db.MetricSnapshots.AsNoTracking()
            .Where(r => r.TakenAt >= since)
            .OrderBy(r => r.TakenAt)
            .ToListAsync(ct);
        MetricPointDto[] history =
        [
            .. rows.GroupBy(r => DateOnly.FromDateTime(r.TakenAt.UtcDateTime))
                .Select(g => MetricPointDto.From(g.Key, g.Last())),
        ];
        return new TriageMetricsDto(
            latest is null ? null : MetricSnapshotDto.From(latest), history, latest is null ? 0 : MetricPointDto.Hours(latest.LlmMillisecondsTotal));
    }

    private Task<MetricSnapshotRow?> LatestAsync(CancellationToken ct) =>
        db.MetricSnapshots.AsNoTracking().OrderByDescending(r => r.TakenAt).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Messages whose canonical sender, List-Id or canonical domain has an approved non-mixed policy, or that a policy
    /// already decided (a <see cref="SuggestionSource.Policy"/> suggestion).
    /// </summary>
    private IQueryable<MessageRow> CoveredByPolicy(IQueryable<MessageRow> live) =>
        live.Where(m => db.SenderPolicies.Any(p => p.Status == PolicyStatus.Approved && !p.IsMixed
                && ((p.Scope == PolicyScope.Sender && p.ScopeKey == m.CanonicalAddress)
                    || (p.Scope == PolicyScope.List && m.ListId != null && p.ScopeKey == m.ListId.Trim().ToLower())
                    || (p.Scope == PolicyScope.Domain && p.ScopeKey == m.CanonicalDomain)))
            || db.Suggestions.Any(s => s.MessageId == m.Id && s.Source == SuggestionSource.Policy));

    /// <summary>
    /// Messages whose raw address an active filter's <c>from</c> matches (an exact address, or an <c>@domain</c> with its
    /// subdomains, as <see cref="FilterCriteriaMapping.FromTermMatches"/>), or whose List-Id a <c>list:</c> query names.
    /// </summary>
    private async Task<int> CoveredByFilterAsync(IQueryable<MessageRow> live, CancellationToken ct)
    {
        var terms = await FilterProposalQuery.ActiveFromTermsAsync(db, ct);
        var lists = (await FilterProposalQuery.ActiveListIdsAsync(db, ct)).ToArray();
        string[] exact = [.. terms.Where(t => t[0] != '@')];
        string[] suffixes = [.. terms.Where(t => t[0] == '@').SelectMany(t => new[] { t, "." + t[1..] })];
        if (exact.Length + suffixes.Length + lists.Length == 0)
        {
            return 0;
        }

        return await live.CountAsync(m => exact.Contains(m.FromAddress)
            || suffixes.Any(x => m.FromAddress.EndsWith(x))
            || (m.ListId != null && lists.Contains(m.ListId.Trim().ToLower())), ct);
    }
}
