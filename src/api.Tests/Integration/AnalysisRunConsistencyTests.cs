using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Run and job state stay consistent: frozen candidates, cancel rules, ended jobs, commits before progress.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisRunConsistencyTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Resume_covers_exactly_the_frozen_candidates_and_skips_those_no_longer_eligible()
    {
        using var stop = new CancellationTokenSource();
        h.Chat.Respond = (ids, call, _, ct) =>
        {
            if (call == 2)
            {
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult(AnalysisRunHarness.Agree(ids));
        };
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync(stop.Token);

        // Newer mail arrives and a not yet analysed candidate is deleted while the run is interrupted.
        await using (var db = postgres.CreateDbContext())
        {
            db.Messages.AddRange(Enumerable.Range(0, 3).Select(i => new MessageRow
            {
                Id = $"n{i:D2}",
                ThreadId = $"t-n{i:D2}",
                FromAddress = AnalysisRunHarness.Other,
                Subject = $"Fresh {i}",
                InternalDate = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                LabelIds = ["INBOX"],
                FetchedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }));
            await db.SaveChangesAsync(Ct);
            await db.Messages.Where(m => m.Id == "c03").ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true), Ct);
        }

        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages, done.FailedMessages).ShouldBe(("completed", 19, 1, 0));
        await using var check = postgres.CreateDbContext();
        (await check.Messages.Where(m => m.Id.StartsWith("n")).Select(m => m.AnalysisStatus).ToListAsync(Ct))
            .ShouldAllBe(s => s == AnalysisStatus.NotAnalysed);
        (await check.Suggestions.AnyAsync(s => s.MessageId == "c03", Ct)).ShouldBeFalse();
        h.Progress(done.JobId!.Value)[^1].ShouldBe(new JobProgress(19, 19, "3 groups, 3 LLM calls"));
    }

    [Fact]
    public async Task Messages_scope_reanalyses_pending_and_rejected_and_skips_decided_ids_in_preview_and_run()
    {
        var first = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Approved);
            await AnalysisRunHarness.DecideAsync(db, "a01", SuggestionStatus.Rejected);
            await AnalysisRunHarness.DecideAsync(db, "b00", SuggestionStatus.Applied);
        }

        string[] ids = ["a00", "a01", "a02", "b00", "x00", "zz00"];
        var preview = await (await h.PostAsync("/api/analysis/preview", new AnalysisPreviewRequest("messages", null, null, ids)))
            .Content.ReadFromJsonAsync<GroupingPreviewDto>(Ct);
        (preview!.Messages, preview.Skipped).ShouldBe((3, 3));

        var run = await h.StartAsync(new StartAnalysisRunRequest("messages", null, ids, null, null));
        run.SkippedMessages.ShouldBe(3);
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages).ShouldBe(("completed", 3, 3));
        await using var check = postgres.CreateDbContext();
        var rows = await check.Suggestions.AsNoTracking().ToDictionaryAsync(s => s.MessageId, Ct);
        rows.Count.ShouldBe(21);
        new[] { "a01", "a02", "x00" }.ShouldAllBe(id => rows[id].RunId == run.Id && rows[id].Status == SuggestionStatus.Pending);
        (rows["a00"].RunId, rows["a00"].Status).ShouldBe((first.Id, SuggestionStatus.Approved));
        (rows["b00"].RunId, rows["b00"].Status).ShouldBe((first.Id, SuggestionStatus.Applied));
        (await check.Messages.SingleAsync(m => m.Id == "a01", Ct)).AnalysisStatus.ShouldBe(AnalysisStatus.Analysed);
    }

    [Fact]
    public async Task A_suggestion_committed_while_the_group_is_stored_is_skipped_not_a_failed_run()
    {
        Task? writer = null;
        h.Chat.Respond = async (ids, _, _, _) =>
        {
            // Another writer inserts a decided suggestion for a member before the store and commits only once the run
            // waits on it: on the member's lock, or without that lock on the unique index, whose violation is retried.
            if (writer is null)
            {
                var inserted = new TaskCompletionSource();
                writer = ApproveConcurrentlyAsync(ids[0], inserted);
                await inserted.Task;
            }

            return AnalysisRunHarness.Agree(ids);
        };
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();
        await writer.ShouldNotBeNull();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages, done.FailedMessages).ShouldBe(("completed", 19, 1, 0));
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Approved && s.RunId == null, Ct)).ShouldBe(1);
        (await check.Suggestions.CountAsync(Ct)).ShouldBe(20);
    }

    [Fact]
    public async Task Cancel_is_409_and_changes_nothing_once_the_job_has_failed()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 5, null));
        await using (var db = postgres.CreateDbContext())
        {
            await db.Jobs.Where(j => j.Id == run.JobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed).SetProperty(j => j.Error, "Model unavailable"), Ct);
            await db.AnalysisRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AnalysisRunStatus.Failed).SetProperty(r => r.Error, "Model unavailable"), Ct);
        }

        (await h.PostAsync($"/api/analysis/runs/{run.Id}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await using var check = postgres.CreateDbContext();
        (await check.Jobs.SingleAsync(j => j.Id == run.JobId, Ct)).Status.ShouldBe(JobStatus.Failed);
        (await check.AnalysisRuns.SingleAsync(r => r.Id == run.Id, Ct)).Status.ShouldBe(AnalysisRunStatus.Failed);
    }

    [Fact]
    public async Task A_job_that_ended_without_its_handler_ends_the_stored_run_and_the_active_filter_agrees()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 5, null));
        await using (var db = postgres.CreateDbContext())
        {
            // As when the guard refuses the job before the handler runs.
            await db.Jobs.Where(j => j.Id == run.JobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.Error, "Signed in to another account")
                .SetProperty(j => j.FinishedAt, DateTimeOffset.UtcNow), Ct);
        }

        var active = await (await h.GetAsync("/api/analysis/runs?active=true")).Content.ReadFromJsonAsync<List<AnalysisRunDto>>(Ct);
        var ended = await (await h.GetAsync("/api/analysis/runs?active=false")).Content.ReadFromJsonAsync<List<AnalysisRunDto>>(Ct);

        active.ShouldBeEmpty();
        var dto = ended.ShouldHaveSingleItem();
        (dto.Status, dto.Error).ShouldBe(("failed", "Signed in to another account"));
        dto.FinishedAt.ShouldNotBeNull();
        (await h.PostAsync($"/api/analysis/runs/{run.Id}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var check = postgres.CreateDbContext();
        (await check.AnalysisRuns.SingleAsync(r => r.Id == run.Id, Ct)).Status.ShouldBe(AnalysisRunStatus.Failed);
    }

    [Fact]
    public async Task A_failed_enqueue_leaves_no_run()
    {
        await using var db = postgres.CreateDbContext();
        var settings = new InMemorySettingsStore();
        settings.Current = settings.Current with { ChatModel = AnalysisRunHarness.ChatModel };
        var service = new AnalysisRunService(db, new ThrowingJobService(), settings, new SenderStatsUpdater(db, TimeProvider.System),
            new LabelCatalog(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System),
            TimeProvider.System);

        await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(AnalysisScope.Inbox, null, null, 5, null, Ct));

        await using var check = postgres.CreateDbContext();
        (await check.AnalysisRuns.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Progress_is_published_only_after_the_group_is_committed()
    {
        var seen = new List<(long Done, int Committed)>();
        h.OnPublish = async job =>
        {
            if (job.Progress is { } progress)
            {
                await using var db = postgres.CreateDbContext();
                var committed = await db.Suggestions.CountAsync(CancellationToken.None);
                lock (seen)
                {
                    seen.Add((progress.Done, committed));
                }
            }
        };
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        seen.Count.ShouldBeGreaterThanOrEqualTo(3);
        seen.ShouldAllBe(x => x.Done == x.Committed);
    }

    [Fact]
    public async Task Representative_bodies_are_fetched_concurrently_within_the_bound()
    {
        var gate = new Lock();
        var (inFlight, max) = (0, 0);
        h.Gmail.BeforeBody = async (_, ct) =>
        {
            lock (gate)
            {
                max = Math.Max(max, ++inFlight);
            }

            await Task.Delay(100, ct);
            lock (gate)
            {
                inFlight--;
            }
        };
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        max.ShouldBeInRange(2, AnalysisRunJob.MaxConcurrentBodyFetches);
    }

    [Fact]
    public async Task Fetch_stats_recompute_the_analysed_count_without_deleted_mail()
    {
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await using var db = postgres.CreateDbContext();
        await db.Messages.Where(m => m.Id == "a00").ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true), Ct);

        await new SenderStatsUpdater(db, TimeProvider.System).UpdateAsync([AnalysisRunHarness.Shop], Ct);

        var sender = await db.Senders.AsNoTracking().SingleAsync(s => s.Address == AnalysisRunHarness.Shop, Ct);
        (sender.TotalCount, sender.AnalysedCount).ShouldBe((9, 9));
    }

    private sealed class ThrowingJobService : IJobService
    {
        public Task<(JobDto Job, bool Created)> EnqueueAsync(
            string type, string queue, object? initialCursor = null, CancellationToken ct = default, string? dedupKey = null) =>
            throw new InvalidOperationException("The job store is unavailable.");

        public Task<JobActionResult> PauseAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<JobActionResult> ResumeAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<JobActionResult> CancelAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<JobDto?> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<JobDto>> ListAsync(bool activeOnly, CancellationToken ct) => throw new NotSupportedException();
    }

    private async Task ApproveConcurrentlyAsync(string messageId, TaskCompletionSource inserted)
    {
        await using var db = postgres.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        var sender = await db.Messages.Where(m => m.Id == messageId).Select(m => m.FromAddress).SingleAsync(Ct);
        var suggestion = new SuggestionRow
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            SenderAddress = sender,
            Source = SuggestionSource.SenderPattern,
            TopicLabel = "Example/Offers",
            Reason = "Synthetic reason",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        suggestion.SetStatus(SuggestionStatus.Approved, new MessageRow { Id = messageId }, DateTimeOffset.UtcNow);
        db.Suggestions.Add(suggestion);
        await db.SaveChangesAsync(Ct);
        inserted.SetResult();
        await h.WaitForLockWaitAsync();
        await tx.CommitAsync(Ct);
    }
}
