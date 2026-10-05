using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Dashboard;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Rules;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Triage metric snapshots (#379): counts over a synthetic mailbox, the 15-minute guard and daily history.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MetricsSnapshotterTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string PolicySender = "news@policy-379.example.com";
    private const string PolicyDomain = "policy-379.example.com";
    private const string FilterDomain = "filter-379.example.com";
    private const string FilterList = "updates.list-379.example.com";

    // Far ahead of real time, so jobs and snapshots other classes leave in the shared database are older than every test step.
    private static readonly DateTimeOffset Start = new(2100, 3, 10, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.MetricSnapshots.ExecuteDeleteAsync(Ct);
        await db.SenderPolicies.ExecuteDeleteAsync(Ct);
        await db.Filters.Where(f => f.DeletedAt == null).ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, Start), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.MetricSnapshots.ExecuteDeleteAsync(CancellationToken.None);
        await db.SenderPolicies.ExecuteDeleteAsync(CancellationToken.None);
        await db.Filters.Where(f => f.Id.StartsWith("metrics-379-")).ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Snapshot_counts_policy_and_filter_coverage_inbox_and_llm_totals()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext();
        var before = await new MetricsSnapshotter(db, clock).TakeAsync(Ct);

        var tag = Guid.NewGuid().ToString("N");
        db.Messages.AddRange(
            Message(tag, 1, PolicySender, inbox: true, unread: true),
            Message(tag, 2, $"other@{PolicyDomain}", inbox: true, unread: false),
            Message(tag, 3, $"alerts@mail.{FilterDomain}", inbox: false, unread: true),
            Message(tag, 4, "digest@unrelated-379.example.com", inbox: false, unread: false, listId: "Updates.List-379.example.com"),
            Message(tag, 5, PolicySender, inbox: true, unread: true, deleted: true));
        // The sender policy covers message 1; the domain policy is mixed, so message 2 is not covered.
        db.SenderPolicies.AddRange(Policy(PolicyScope.Sender, PolicySender, isMixed: false), Policy(PolicyScope.Domain, PolicyDomain, isMixed: true));
        db.Filters.AddRange(
            Filter(tag, "a", new GmailFilterCriteria(From: $"@{FilterDomain}")),
            Filter(tag, "b", new GmailFilterCriteria(Query: $"list:<{FilterList}>")));
        db.AnalysisRuns.Add(new AnalysisRunRow
        {
            Id = Guid.NewGuid(),
            Scope = AnalysisScope.Inbox,
            LlmMilliseconds = 1_800_000,
            PromptTokens = 4_000,
            CreatedAt = Start,
        });
        await db.SaveChangesAsync(Ct);

        clock.Advance(TimeSpan.FromMinutes(1));
        var after = await new MetricsSnapshotter(db, clock).TakeAsync(Ct);

        (after.MessagesTotal - before.MessagesTotal).ShouldBe(4);
        (after.InboxCount - before.InboxCount).ShouldBe(2);
        (after.InboxUnreadCount - before.InboxUnreadCount).ShouldBe(1);
        (after.CoveredByPolicy - before.CoveredByPolicy).ShouldBe(1);
        (after.CoveredByFilter - before.CoveredByFilter).ShouldBe(2);
        (after.LlmMillisecondsTotal - before.LlmMillisecondsTotal).ShouldBe(1_800_000);
        (after.PromptTokensTotal - before.PromptTokensTotal).ShouldBe(4_000);
        after.TakenAt.ShouldBe(Start.AddMinutes(1));

        await db.Messages.Where(m => m.Id.StartsWith(tag)).ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task Policy_coverage_uses_the_winning_policy_sender_then_list_then_domain()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext();
        var before = await new MetricsSnapshotter(db, clock).TakeAsync(Ct);

        var tag = Guid.NewGuid().ToString("N");
        db.Messages.AddRange(
            Message(tag, 1, PolicySender, inbox: true, unread: false),
            Message(tag, 2, $"other@{PolicyDomain}", inbox: true, unread: false),
            Message(tag, 3, $"digest@{PolicyDomain}", inbox: true, unread: false, listId: FilterList));
        // The mixed sender policy wins over the non-mixed domain policy for message 1; message 2 falls through to the
        // domain policy; the mixed list policy wins for message 3.
        db.SenderPolicies.AddRange(
            Policy(PolicyScope.Sender, PolicySender, isMixed: true),
            Policy(PolicyScope.List, FilterList, isMixed: true),
            Policy(PolicyScope.Domain, PolicyDomain, isMixed: false));
        await db.SaveChangesAsync(Ct);

        clock.Advance(TimeSpan.FromMinutes(1));
        var after = await new MetricsSnapshotter(db, clock).TakeAsync(Ct);

        (after.CoveredByPolicy - before.CoveredByPolicy).ShouldBe(1);

        await db.Messages.Where(m => m.Id.StartsWith(tag)).ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task Scheduled_snapshots_wait_15_minutes_and_then_follow_completed_jobs_or_the_hour()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext();
        var snapshotter = new MetricsSnapshotter(db, clock);

        (await snapshotter.TakeIfDueAsync(Ct)).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromMinutes(5));
        db.Jobs.Add(CompletedJob(MailboxFetchJob.JobType, clock.GetUtcNow()));
        await db.SaveChangesAsync(Ct);

        clock.Advance(TimeSpan.FromMinutes(5));
        (await snapshotter.TakeIfDueAsync(Ct)).ShouldBeNull();

        // The fetch completed inside the window; the first check after it takes the snapshot.
        clock.Advance(TimeSpan.FromMinutes(5));
        (await snapshotter.TakeIfDueAsync(Ct)).ShouldNotBeNull();

        clock.Advance(TimeSpan.FromMinutes(30));
        (await snapshotter.TakeIfDueAsync(Ct)).ShouldBeNull();

        clock.Advance(TimeSpan.FromMinutes(30));
        (await snapshotter.TakeIfDueAsync(Ct)).ShouldNotBeNull();
        (await db.MetricSnapshots.CountAsync(Ct)).ShouldBe(3);

        await db.Jobs.Where(j => j.CreatedAt >= Start).ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task History_keeps_the_last_snapshot_of_each_day_for_90_days_and_old_rows_are_pruned()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext();
        db.MetricSnapshots.AddRange(
            Snapshot(Start.AddDays(-500), inbox: 1),
            Snapshot(Start.AddDays(-100), inbox: 2),
            Snapshot(Start.AddDays(-2).AddHours(-1), inbox: 3),
            Snapshot(Start.AddDays(-2).AddHours(10), inbox: 4),
            Snapshot(Start.AddDays(-1), inbox: 5));
        await db.SaveChangesAsync(Ct);
        var snapshotter = new MetricsSnapshotter(db, clock);

        await snapshotter.TakeAsync(Ct);
        var metrics = await snapshotter.GetAsync(Ct);

        metrics.History.Select(p => p.InboxCount).Take(2).ShouldBe([4, 5]);
        metrics.History.Select(p => p.Day).ShouldBe(
            [DateOnly.FromDateTime(Start.AddDays(-2).UtcDateTime), DateOnly.FromDateTime(Start.AddDays(-1).UtcDateTime), DateOnly.FromDateTime(Start.UtcDateTime)]);
        metrics.Current.ShouldNotBeNull().TakenAt.ShouldBe(Start);
        (await db.MetricSnapshots.CountAsync(Ct)).ShouldBe(5);
    }

    [Fact]
    public async Task Endpoints_take_a_manual_snapshot_and_return_it()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        var posted = await client.PostAsync(new Uri("/api/dashboard/triage/snapshot", UriKind.Relative), null, Ct);
        posted.EnsureSuccessStatusCode();
        var metrics = await client.GetFromJsonAsync<TriageMetricsDto>(new Uri("/api/dashboard/triage", UriKind.Relative), Ct);

        metrics.ShouldNotBeNull().Current.ShouldNotBeNull();
        metrics.History.ShouldNotBeEmpty();
    }

    private static MessageRow Message(string tag, int n, string from, bool inbox, bool unread, string? listId = null, bool deleted = false) => new()
    {
        Id = $"{tag}-{n}",
        ThreadId = $"{tag}-{n}",
        FromAddress = from,
        CanonicalAddress = from,
        CanonicalDomain = from[(from.IndexOf('@', StringComparison.Ordinal) + 1)..],
        LabelIds = [.. new[] { inbox ? FilterSpec.Inbox : null, unread ? FilterSpec.Unread : null }.OfType<string>()],
        ListId = listId,
        DeletedInGmail = deleted,
        InternalDate = Start,
        FetchedAt = Start,
        UpdatedAt = Start,
    };

    private static SenderPolicyRow Policy(PolicyScope scope, string key, bool isMixed) => new()
    {
        Id = Guid.NewGuid(),
        Scope = scope,
        ScopeKey = key,
        IsMixed = isMixed,
        TopicLabel = "Example/News",
        Action = PolicyAction.Archive,
        Status = PolicyStatus.Approved,
        Reason = "Synthetic",
        CreatedAt = Start,
    };

    private static FilterRow Filter(string tag, string suffix, GmailFilterCriteria criteria) => new()
    {
        Id = $"metrics-379-{tag}-{suffix}",
        Criteria = FilterRow.WriteCriteria(criteria),
        Action = FilterRow.WriteAction(new GmailFilterAction(["Label_1"], [])),
        CriteriaSummary = "synthetic",
        FirstSeenAt = Start,
        LastSeenAt = Start,
        UpdatedAt = Start,
    };

    private static JobRow CompletedJob(string type, DateTimeOffset finishedAt) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Queue = "metrics-379",
        Status = JobStatus.Completed,
        CreatedAt = finishedAt,
        QueuedAt = finishedAt,
        StartedAt = finishedAt,
        UpdatedAt = finishedAt,
        FinishedAt = finishedAt,
    };

    private static MetricSnapshotRow Snapshot(DateTimeOffset takenAt, int inbox) =>
        new() { Id = Guid.NewGuid(), TakenAt = takenAt, InboxCount = inbox };
}
