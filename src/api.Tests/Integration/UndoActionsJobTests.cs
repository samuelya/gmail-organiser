using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Common;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Apply, then undo, over the harness mailbox (every message starts in INBOX) with synthetic labels.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class UndoActionsJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string ActionLabel = "Synthetic Action";
    private const string DeleteLabel = "Synthetic Delete";
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ActionLog.ExecuteDeleteAsync();
            await db.ActionBatches.ExecuteDeleteAsync();
        }

        await h.InitializeAsync();
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
            .UpdateAsync(s => s with { ActionLabelName = ActionLabel, DeleteLabelName = DeleteLabel }, Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Apply_then_undo_restores_every_label_set_and_keeps_the_labels_it_created()
    {
        await SeedAsync(("a00", "Example/Nested/Water", false, false), ("b00", "Fresh/Topic", true, false), ("c00", "Fresh/Topic", false, true));
        await StarAsync("c01");
        await SeedAsync(("c01", "Fresh/Topic", false, true));
        string[] ids = ["a00", "b00", "c00", "c01"];
        var before = ids.ToDictionary(id => id, Labels);
        var apply = await ApplyAndRunAsync();
        Labels("c00").ShouldNotContain("INBOX");

        var undo = await UndoAsync(apply.Id);
        (undo.Kind, undo.Description, undo.UndoOf, undo.CanUndo).ShouldBe(("undo", $"Undo: {apply.Description}", apply.Id, false));
        await h.RunNextAsync();

        foreach (var id in ids)
        {
            Labels(id).ShouldBe(before[id], ignoreOrder: true);
        }

        await using var db = postgres.CreateDbContext();
        foreach (var id in ids)
        {
            (await db.Messages.SingleAsync(m => m.Id == id, Ct)).LabelIds.ShouldBe(before[id], ignoreOrder: true);
        }

        (await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Approved, Ct)).ShouldBe(4);
        (await db.Messages.CountAsync(m => m.AnalysisStatus == Fetch.AnalysisStatus.Approved, Ct)).ShouldBe(4);
        (await db.Senders.SumAsync(s => s.AppliedCount, Ct)).ShouldBe(0);
        (await db.ActionLog.CountAsync(l => l.BatchId == apply.Id && l.UndoneByBatchId == undo.Id, Ct)).ShouldBe(4);
        (await db.ActionBatches.SingleAsync(b => b.Id == undo.Id, Ct)).MessageCount.ShouldBe(4);
        (await JobAsync(undo)).Status.ShouldBe(JobStatus.Completed);

        // Undo never deletes labels: the ones the apply created stay, and History lists them.
        var labels = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        string[] created = ["Example/Nested/Water", "Fresh", "Fresh/Topic", ActionLabel, DeleteLabel];
        created.ShouldAllBe(name => labels.ContainsKey(name));

        var detail = await DetailAsync(apply.Id);
        detail.Batch.UndoneAt.ShouldNotBeNull();
        detail.Batch.CanUndo.ShouldBeFalse();
        detail.Rows.ShouldAllBe(r => r.UndoneByBatchId == undo.Id);
        detail.CreatedLabels.Select(l => (l.Id, l.Name)).ShouldBe(created.Select(n => (labels[n], n)), ignoreOrder: true);
    }

    [Fact]
    public async Task A_rate_limit_leaves_a_partial_undo_that_can_be_undone_again()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync([.. Enumerable.Range(0, 4).Select(i => ($"a{i:D2}", "Example", false, false))]);
        var apply = await ApplyAndRunAsync();
        h.Gmail.BeforeBatchModify = (call, _) =>
            call == 4 ? throw new GmailRateLimitedException("Synthetic rate limit.") : Task.CompletedTask;

        var undo = await UndoAsync(apply.Id);
        await h.RunNextAsync();

        (await JobAsync(undo)).Status.ShouldBe(JobStatus.Failed);
        var detail = await DetailAsync(apply.Id);
        (detail.Batch.UndoneAt, detail.Batch.CanUndo).ShouldBe((null, true));
        detail.Rows.Count(r => r.UndoneByBatchId == undo.Id).ShouldBe(2);
        Enumerable.Range(0, 4).Count(i => Labels($"a{i:D2}").Contains("INBOX")).ShouldBe(2);
        await using (var db = postgres.CreateDbContext())
        {
            // The refused chunk was reverted: only the reverted messages are logged under the undo.
            (await db.ActionLog.CountAsync(l => l.BatchId == undo.Id, Ct)).ShouldBe(2);
            (await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Applied, Ct)).ShouldBe(2);
        }

        h.Gmail.BeforeBatchModify = null;
        var again = await UndoAsync(apply.Id);
        await h.RunNextAsync();

        Enumerable.Range(0, 4).ShouldAllBe(i => Labels($"a{i:D2}").Contains("INBOX"));
        var done = await DetailAsync(apply.Id);
        done.Batch.UndoneAt.ShouldNotBeNull();
        done.Rows.Count(r => r.UndoneByBatchId == again.Id).ShouldBe(2);
    }

    [Fact]
    public async Task A_restart_mid_undo_resends_the_pending_chunk()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync([.. Enumerable.Range(0, 4).Select(i => ($"a{i:D2}", "Example", false, false))]);
        var apply = await ApplyAndRunAsync();
        using var stop = new CancellationTokenSource();
        h.Gmail.BeforeBatchModify = (call, _) => call == 4 ? Stop(stop) : Task.CompletedTask;
        var undo = await UndoAsync(apply.Id);

        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        h.Gmail.BatchModifyCalls.ElementAt(3).ShouldBe(h.Gmail.BatchModifyCalls.ElementAt(4));
        Enumerable.Range(0, 4).ShouldAllBe(i => Labels($"a{i:D2}").Contains("INBOX"));
        await using var db = postgres.CreateDbContext();
        (await db.ActionLog.CountAsync(l => l.BatchId == undo.Id, Ct)).ShouldBe(4);
        (await db.ActionBatches.SingleAsync(b => b.Id == undo.Id, Ct)).MessageCount.ShouldBe(4);
        (await db.Senders.SumAsync(s => s.AppliedCount, Ct)).ShouldBe(0);
        h.Progress(undo.JobId!.Value)[^1].ShouldBe(new JobProgress(4, 4, "Undid 4 of 4 messages"));
    }

    [Fact]
    public async Task A_resent_chunk_drops_a_label_deleted_in_gmail_meanwhile()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync([.. Enumerable.Range(0, 4).Select(i => ($"a{i:D2}", "Example", false, false))]);
        var apply = await ApplyAndRunAsync();
        using var stop = new CancellationTokenSource();
        IReadOnlyList<string> pending = [];
        h.Gmail.BeforeBatchModify = (call, ids) =>
        {
            if (call != 4)
            {
                return Task.CompletedTask;
            }

            pending = ids;
            return Stop(stop);
        };
        var undo = await UndoAsync(apply.Id);
        await h.RunNextAsync(stop.Token);

        var example = (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == "Example").Id;
        h.Gmail.Inner.DeleteLabel(example);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        (await JobAsync(undo)).Status.ShouldBe(JobStatus.Completed);
        Enumerable.Range(0, 4).ShouldAllBe(i => Labels($"a{i:D2}").Contains("INBOX"));
        (await DetailAsync(apply.Id)).Batch.UndoneAt.ShouldNotBeNull();
        await using var db = postgres.CreateDbContext();
        var rows = await db.ActionLog.AsNoTracking().Where(l => l.BatchId == undo.Id).ToListAsync(Ct);
        rows.Count.ShouldBe(4);
        pending.Count.ShouldBe(2);
        rows.Where(r => pending.Contains(r.MessageId)).ShouldAllBe(r => r.LabelsAdded.SequenceEqual(new[] { "INBOX" }) && !r.LabelsRemoved.Contains("Example"));
        rows.ShouldAllBe(r => !r.LabelIdsAfter.Contains(example));
        await StoredLabelsMatchGmailAsync(db, 4);
    }

    [Fact]
    public async Task An_undo_after_a_label_was_deleted_in_gmail_drops_it_from_the_stored_labels()
    {
        await SeedAsync([.. Enumerable.Range(0, 3).Select(i => ($"a{i:D2}", "Example", false, false))]);
        var apply = await ApplyAndRunAsync();
        var example = (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == "Example").Id;
        h.Gmail.Inner.DeleteLabel(example);

        var undo = await UndoAsync(apply.Id);
        await h.RunNextAsync();

        (await JobAsync(undo)).Status.ShouldBe(JobStatus.Completed);
        await using var db = postgres.CreateDbContext();
        var rows = await db.ActionLog.AsNoTracking().Where(l => l.BatchId == undo.Id).ToListAsync(Ct);
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(r => !r.LabelIdsAfter.Contains(example) && r.LabelIdsAfter.Contains("INBOX"));
        await StoredLabelsMatchGmailAsync(db, 3);
    }

    [Fact]
    public async Task Messages_gone_from_gmail_are_skipped_with_a_note()
    {
        await SeedAsync(("a00", "Example", false, false), ("a01", "Example", false, false), ("a02", "Example", false, false));
        var apply = await ApplyAndRunAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "a00").ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true), Ct);
        }

        h.Gmail.BeforeBatchModify = (_, ids) => ids.Contains("a01")
            ? throw GmailRetryPolicy.CreateApiException(HttpStatusCode.NotFound, "notFound")
            : Task.CompletedTask;
        var applyCalls = h.Gmail.BatchModifyCalls.Count;
        var undo = await UndoAsync(apply.Id);
        await h.RunNextAsync();

        Labels("a02").ShouldContain("INBOX");
        h.Gmail.BatchModifyCalls.Skip(applyCalls).SelectMany(c => c).ShouldNotContain("a00");
        var rows = (await DetailAsync(undo.Id)).Rows.ToDictionary(r => r.MessageId);
        rows["a00"].Note.ShouldBe(UndoActionsJob.GoneNote);
        rows["a01"].Note.ShouldBe(UndoActionsJob.GoneNote);
        rows["a02"].LabelsAdded.ShouldBe(["INBOX"]);
        rows["a02"].Subject.ShouldNotBeNull();
        (await DetailAsync(apply.Id)).Batch.UndoneAt.ShouldNotBeNull();
        h.Progress(undo.JobId!.Value)[^1].Message.ShouldBe($"Undid 1 of 3 messages; 2 skipped ({UndoActionsJob.GoneNote})");
        await using var after = postgres.CreateDbContext();
        (await after.Messages.SingleAsync(m => m.Id == "a01", Ct)).DeletedInGmail.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_undo_and_an_undo_of_an_undo_are_conflicts()
    {
        await SeedAsync(("a00", "Example", false, false));
        var apply = await ApplyAndRunAsync();

        var undo = await UndoAsync(apply.Id);
        (await PostUndoAsync(apply.Id)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await h.RunNextAsync();
        (await PostUndoAsync(apply.Id)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostUndoAsync(undo.Id)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PostUndoAsync(Guid.NewGuid())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task History_pages_newest_first_and_caps_the_rows()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var db = postgres.CreateDbContext())
        {
            for (var i = 0; i < 3; i++)
            {
                db.ActionBatches.Add(new ActionBatchRow
                {
                    Id = Guid.NewGuid(),
                    Kind = ActionKind.Apply,
                    Description = $"Batch {i}",
                    MessageCount = 1,
                    CreatedAt = now.AddMinutes(i),
                });
            }

            await db.SaveChangesAsync(Ct);
            var big = await db.ActionBatches.SingleAsync(b => b.Description == "Batch 0", Ct);
            db.ActionLog.AddRange(Enumerable.Range(0, HistoryQuery.MaxRows + 1).Select(i => new ActionLogRow
            {
                Id = Guid.CreateVersion7(now.AddMilliseconds(i)),
                BatchId = big.Id,
                MessageId = $"m{i}",
                LabelIdsBefore = ["INBOX"],
                CreatedAt = now,
            }));
            await db.SaveChangesAsync(Ct);
        }

        var first = await GetAsync<PagedDto<ActionBatchDto>>("/api/history?page=1&pageSize=2");
        (first.Total, first.Page, first.PageSize).ShouldBe((3L, 1, 2));
        first.Items.Select(b => b.Description).ShouldBe(["Batch 2", "Batch 1"]);
        var last = (await GetAsync<PagedDto<ActionBatchDto>>("/api/history?page=2&pageSize=2")).Items.ShouldHaveSingleItem();
        last.Description.ShouldBe("Batch 0");
        (await h.GetAsync("/api/history?pageSize=0")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.GetAsync($"/api/history/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var detail = await DetailAsync(last.Id);
        detail.Rows.Count.ShouldBe(HistoryQuery.MaxRows);
        detail.Truncated.ShouldBeTrue();
        detail.Rows[0].LabelsRemoved.ShouldBe(["INBOX"]);
        detail.Rows[0].LabelNamesRemoved.ShouldBe(["INBOX"]);
    }

    private static Task Stop(CancellationTokenSource stop)
    {
        stop.Cancel();
        stop.Token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private async Task<ActionBatchDto> ApplyAndRunAsync()
    {
        var response = await h.PostAsync("/api/review/apply", new ApplyRequest());
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var batch = (await response.Content.ReadFromJsonAsync<ActionBatchDto>(Ct)).ShouldNotBeNull();
        await h.RunNextAsync();
        return (await DetailAsync(batch.Id)).Batch;
    }

    private Task<HttpResponseMessage> PostUndoAsync(Guid id) => h.PostAsync($"/api/history/{id}/undo", new { });

    private async Task<ActionBatchDto> UndoAsync(Guid id)
    {
        var response = await PostUndoAsync(id);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<ActionBatchDto>(Ct)).ShouldNotBeNull();
    }

    private Task<ActionBatchDetailDto> DetailAsync(Guid id) => GetAsync<ActionBatchDetailDto>($"/api/history/{id}");

    private async Task<T> GetAsync<T>(string path)
        where T : class
    {
        var response = await h.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }

    private async Task<JobRow> JobAsync(ActionBatchDto batch)
    {
        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == batch.JobId, Ct);
        (job.Type, job.Queue, job.DedupKey).ShouldBe((ReviewJobTypes.Undo, JobQueues.Apply, batch.Id.ToString()));
        return job;
    }

    private async Task SeedAsync(params (string Id, string Topic, bool NeedsAction, bool ToBeDeleted)[] rows)
    {
        await using var db = postgres.CreateDbContext();
        var decidedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var row in rows)
        {
            var message = await db.Messages.SingleAsync(m => m.Id == row.Id, Ct);
            var suggestion = new SuggestionRow
            {
                Id = Guid.NewGuid(),
                MessageId = row.Id,
                SenderAddress = message.FromAddress,
                Source = SuggestionSource.Llm,
                TopicLabel = row.Topic,
                NeedsAction = row.NeedsAction,
                ToBeDeleted = row.ToBeDeleted,
                Confidence = 0.9,
                Reason = "Synthetic reason",
                CreatedAt = decidedAt,
            };
            suggestion.SetStatus(SuggestionStatus.Approved, message, decidedAt);
            db.Suggestions.Add(suggestion);
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task StarAsync(string id)
    {
        string[] labels = ["INBOX", "CATEGORY_UPDATES", "STARRED"];
        h.Gmail.Inner.SetLabels(id, labels);
        await using var db = postgres.CreateDbContext();
        await db.Messages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, labels), Ct);
    }

    private async Task StoredLabelsMatchGmailAsync(AppDbContext db, int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => $"a{i:D2}").ToArray();
        var stored = await db.Messages.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(Ct);
        stored.Count.ShouldBe(count);
        stored.ShouldAllBe(m => m.LabelIds.Order().SequenceEqual(Labels(m.Id).Order()));
    }

    private IReadOnlyList<string> Labels(string id) => h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds;
}
