using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Apply over the harness mailbox (every message starts in INBOX) with synthetic action and delete labels.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ApplyActionsJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string ActionLabel = "Synthetic Action/Open";
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
    public async Task Mixed_outcomes_create_labels_parent_first_and_log_before_and_after()
    {
        await h.Gmail.Inner.CreateLabelAsync("synthetic action", Ct);
        await SeedAsync(
            ("a00", "Example/Nested/Water", false, false, SuggestionStatus.Approved),
            ("b00", "Fresh/Topic", true, false, SuggestionStatus.Approved),
            ("c00", "Fresh/Topic", false, true, SuggestionStatus.Approved),
            ("c01", "Fresh/Topic", false, true, SuggestionStatus.Approved),
            ("x00", "Fresh/Other", false, false, SuggestionStatus.Pending));
        await StarAsync("c01");

        var batch = await ApplyAsync(new ApplyRequest());
        (batch.Kind, batch.Description, batch.MessageCount).ShouldBe(("apply", "Apply 4 suggestions", 0));
        await h.RunNextAsync();

        var created = h.Gmail.CreateLabelCalls.ToList();
        created.ShouldBe(["Example/Nested/Water", "Fresh", "Fresh/Topic", "synthetic action/Open", DeleteLabel], ignoreOrder: true);
        created.IndexOf("Fresh").ShouldBeLessThan(created.IndexOf("Fresh/Topic"));
        var ids = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        Labels("a00").ShouldBe(["CATEGORY_UPDATES", ids["Example/Nested/Water"]], ignoreOrder: true);
        Labels("b00").ShouldBe(["INBOX", "CATEGORY_UPDATES", ids["Fresh/Topic"], ids["synthetic action/Open"]], ignoreOrder: true);
        Labels("c00").ShouldBe(["CATEGORY_UPDATES", ids["Fresh/Topic"], ids[DeleteLabel]], ignoreOrder: true);
        Labels("c01").ShouldBe(["CATEGORY_UPDATES", "STARRED", ids["Fresh/Topic"]], ignoreOrder: true);
        Labels("x00").ShouldBe(["INBOX", "CATEGORY_UPDATES"]);

        await using var db = postgres.CreateDbContext();
        var log = await db.ActionLog.Where(l => l.BatchId == batch.Id).ToDictionaryAsync(l => l.MessageId, Ct);
        log.Keys.ShouldBe(["a00", "b00", "c00", "c01"], ignoreOrder: true);
        log["c00"].LabelIdsBefore.ShouldBe(["INBOX", "CATEGORY_UPDATES"]);
        log["c00"].LabelIdsAfter.ShouldBe(Labels("c00"), ignoreOrder: true);
        log["c00"].LabelsAdded.ShouldBe(["Fresh/Topic", DeleteLabel], ignoreOrder: true);
        log["c00"].LabelsRemoved.ShouldBe(["INBOX"]);
        log["c01"].Note.ShouldBe("protected: starred");
        log["c01"].LabelsAdded.ShouldNotContain(DeleteLabel);
        log["b00"].LabelsRemoved.ShouldBeEmpty();
        foreach (var id in log.Keys)
        {
            (await db.Messages.SingleAsync(m => m.Id == id, Ct)).LabelIds.ShouldBe(Labels(id), ignoreOrder: true);
        }

        (await db.Suggestions.Where(s => s.Status == SuggestionStatus.Applied).CountAsync(Ct)).ShouldBe(4);
        (await db.Messages.CountAsync(m => m.AnalysisStatus == Fetch.AnalysisStatus.Applied, Ct)).ShouldBe(4);
        (await db.Suggestions.SingleAsync(s => s.MessageId == "x00", Ct)).Status.ShouldBe(SuggestionStatus.Pending);
        (await db.Senders.SingleAsync(s => s.Address == AnalysisRunHarness.Billing, Ct)).AppliedCount.ShouldBe(2);
        var stored = await db.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct);
        stored.MessageCount.ShouldBe(4);
        stored.CreatedLabelIds.ShouldBe(created.Select(name => ids[name]), ignoreOrder: true);
        (await JobAsync(batch)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task Chunks_respect_the_cap_and_progress_counts_messages()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 3;
        await SeedAsync([.. Enumerable.Range(0, 7).Select(i => ($"a{i:D2}", "Example", false, false, SuggestionStatus.Approved))]);
        await SeedAsync(("b00", "Example", true, false, SuggestionStatus.Approved));

        var batch = await ApplyAsync(new ApplyRequest());
        await h.RunNextAsync();

        h.Gmail.BatchModifyCalls.Select(c => c.Count).ShouldBe([3, 3, 1, 1], ignoreOrder: true);
        var progress = h.Progress(batch.JobId!.Value);
        progress[^1].ShouldBe(new JobProgress(8, 8, "Applied 8 of 8 messages"));
        progress.ShouldAllBe(p => p.Total == 8);
        progress.Select(p => p.Done).ShouldBeInOrder();
    }

    [Fact]
    public async Task A_restart_after_the_log_is_written_resends_the_pending_chunk()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync([.. Enumerable.Range(0, 6).Select(i => ($"a{i:D2}", "Example", false, false, SuggestionStatus.Approved))]);
        using var stop = new CancellationTokenSource();
        h.Gmail.BeforeBatchModify = (call, _) => call == 2 ? Stop(stop) : Task.CompletedTask;
        var batch = await ApplyAsync(new ApplyRequest());

        await h.RunNextAsync(stop.Token);
        await using (var db = postgres.CreateDbContext())
        {
            // The interrupted chunk is logged and applied locally before Gmail saw it.
            (await db.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(4);
            (await db.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct)).MessageCount.ShouldBe(2);
        }

        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        h.Gmail.BatchModifyCalls.Select(c => c.Count).ShouldBe([2, 2, 2, 2]);
        h.Gmail.BatchModifyCalls.ElementAt(1).ShouldBe(h.Gmail.BatchModifyCalls.ElementAt(2));
        foreach (var id in new[] { "a00", "a01", "a02", "a03", "a04", "a05" })
        {
            Labels(id).ShouldNotContain("INBOX");
        }

        // The resumed run counts the chunks still to come, not only the pending one.
        h.Progress(batch.JobId!.Value).ShouldAllBe(p => p.Total == 6);
        await using var after = postgres.CreateDbContext();
        (await after.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(6);
        (await after.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct)).MessageCount.ShouldBe(6);
        (await after.Senders.SingleAsync(s => s.Address == AnalysisRunHarness.Shop, Ct)).AppliedCount.ShouldBe(6);
        (await JobAsync(batch)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task A_rate_limited_chunk_is_reverted_the_batch_marked_partial_and_resume_finishes()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync([.. Enumerable.Range(0, 4).Select(i => ($"a{i:D2}", "Example", false, false, SuggestionStatus.Approved))]);
        h.Gmail.BeforeBatchModify = (call, _) =>
            call == 2 ? throw new GmailRateLimitedException("Synthetic rate limit.") : Task.CompletedTask;
        var batch = await ApplyAsync(new ApplyRequest(AnalysisRunHarness.Shop));
        batch.Description.ShouldBe($"Apply 4 suggestions for {AnalysisRunHarness.Shop}");

        await h.RunNextAsync();

        var job = await JobAsync(batch);
        (job.Status, job.Error).ShouldBe((JobStatus.Failed, "Synthetic rate limit."));
        await using (var db = postgres.CreateDbContext())
        {
            var stored = await db.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct);
            (stored.Description, stored.MessageCount).ShouldBe(($"{batch.Description} (partial: 2 of 4)", 2));
            var logged = await db.ActionLog.Where(l => l.BatchId == batch.Id).Select(l => l.MessageId).ToListAsync(Ct);
            logged.ShouldBe(h.Gmail.BatchModifyCalls.First(), ignoreOrder: true);
            var approved = await db.Suggestions.Where(s => s.Status == SuggestionStatus.Approved).Select(s => s.MessageId).ToListAsync(Ct);
            approved.ShouldBe(h.Gmail.BatchModifyCalls.Last(), ignoreOrder: true);
            logged.ShouldAllBe(id => !Labels(id).Contains("INBOX"));
            approved.ShouldAllBe(id => Labels(id).Contains("INBOX"));
        }

        await using (var scope = h.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IJobService>().ResumeAsync(job.Id, Ct)).ShouldBe(JobActionResult.Ok);
        }

        await h.RunNextAsync();

        await using var after = postgres.CreateDbContext();
        var done = await after.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct);
        (done.Description, done.MessageCount).ShouldBe((batch.Description, 4));
        (await after.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(4);
        new[] { "a00", "a01", "a02", "a03" }.ShouldAllBe(id => !Labels(id).Contains("INBOX"));
    }

    [Fact]
    public async Task Apply_validates_filters_and_skips_labels_gmail_refuses()
    {
        (await h.PostAsync("/api/review/apply", new ApplyRequest())).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PostAsync("/api/review/apply", new ApplyRequest(SuggestionIds: []))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/apply", new ApplyRequest(" "))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await SeedAsync(
            ("a00", "INBOX/Sub", false, false, SuggestionStatus.Approved),
            ("a01", "Example", false, false, SuggestionStatus.Approved),
            ("a02", "Example", false, false, SuggestionStatus.Approved),
            ("a03", "Example", false, false, SuggestionStatus.Rejected));
        await using (var db = postgres.CreateDbContext())
        {
            var a02 = await db.Suggestions.Where(s => s.MessageId == "a02").Select(s => s.Id).SingleAsync(Ct);
            var a00 = await db.Suggestions.Where(s => s.MessageId == "a00").Select(s => s.Id).SingleAsync(Ct);
            var batch = await ApplyAsync(new ApplyRequest(AnalysisRunHarness.Shop.ToUpperInvariant(), [a00, a02, a02]));
            batch.Description.ShouldBe($"Apply 1 suggestion for {AnalysisRunHarness.Shop}");
            await h.RunNextAsync();

            var progress = h.Progress(batch.JobId!.Value);
            progress[^1].Message.ShouldBe("Applied 1 of 1 messages; 1 skipped (invalid label)");
            progress.ShouldAllBe(p => p.Message!.EndsWith("; 1 skipped (invalid label)", StringComparison.Ordinal));
        }

        h.Gmail.CreateLabelCalls.ShouldBeEmpty();
        Labels("a00").ShouldContain("INBOX");
        Labels("a01").ShouldContain("INBOX");
        Labels("a02").ShouldNotContain("INBOX");
        await using var after = postgres.CreateDbContext();
        (await after.Suggestions.SingleAsync(s => s.MessageId == "a00", Ct)).Status.ShouldBe(SuggestionStatus.Approved);
        (await after.Suggestions.SingleAsync(s => s.MessageId == "a03", Ct)).Status.ShouldBe(SuggestionStatus.Rejected);
    }

    [Fact]
    public async Task A_resent_chunk_that_fails_keeps_its_undo_log_and_must_be_resumed_not_cancelled()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync([.. Enumerable.Range(0, 4).Select(i => ($"a{i:D2}", "Example", false, false, SuggestionStatus.Approved))]);
        using var stop = new CancellationTokenSource();
        h.Gmail.BeforeBatchModify = (call, _) => call switch
        {
            2 => Stop(stop),
            3 => throw new GmailRateLimitedException("Synthetic rate limit."),
            _ => Task.CompletedTask,
        };
        var batch = await ApplyAsync(new ApplyRequest());
        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);

        await h.RunNextAsync();

        var job = await JobAsync(batch);
        job.Status.ShouldBe(JobStatus.Failed);
        JsonSerializer.Deserialize<ApplyCursor>(job.Cursor!, JsonSerializerOptions.Web)!.Pending.ShouldNotBeNull();
        await using (var db = postgres.CreateDbContext())
        {
            // The first send may have landed: the log and the applied state stay.
            (await db.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(4);
            (await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Applied, Ct)).ShouldBe(4);
            var stored = await db.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct);
            (stored.Description, stored.MessageCount).ShouldBe(($"{batch.Description} (partial: 2 of 4)", 2));
        }

        (await RefusesConnectAsync()).ShouldBeTrue("a failed apply with a pending chunk");
        var cancel = await h.PostAsync($"/api/jobs/{job.Id}/cancel", new { });
        cancel.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await cancel.Content.ReadAsStringAsync(Ct)).ShouldContain("resume it");

        await ResumeAsync(job.Id);
        await h.RunNextAsync();

        (await JobAsync(batch)).Status.ShouldBe(JobStatus.Completed);
        new[] { "a00", "a01", "a02", "a03" }.ShouldAllBe(id => !Labels(id).Contains("INBOX"));
        await using var after = postgres.CreateDbContext();
        var done = await after.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct);
        (done.Description, done.MessageCount).ShouldBe((batch.Description, 4));
        (await RefusesConnectAsync()).ShouldBeFalse("the apply finished");
    }

    [Fact]
    public async Task Cancelling_a_queued_apply_marks_the_batch_partial_and_connect_is_refused_until_then()
    {
        await SeedAsync(("a00", "Example", false, false, SuggestionStatus.Approved), ("a01", "Example", false, false, SuggestionStatus.Approved));
        var batch = await ApplyAsync(new ApplyRequest());
        (await RefusesConnectAsync()).ShouldBeTrue("a queued apply");

        (await h.PostAsync($"/api/jobs/{batch.JobId}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await JobAsync(batch)).Status.ShouldBe(JobStatus.Cancelled);
        await using var db = postgres.CreateDbContext();
        (await db.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct)).Description.ShouldBe($"{batch.Description} (partial: 0 of 2)");
        (await RefusesConnectAsync()).ShouldBeFalse("the apply was cancelled");
        h.Gmail.BatchModifyCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_message_gmail_no_longer_has_is_skipped_and_the_rest_of_its_chunk_applied()
    {
        await SeedAsync([.. Enumerable.Range(0, 3).Select(i => ($"a{i:D2}", "Example", false, false, SuggestionStatus.Approved))]);
        h.Gmail.BeforeBatchModify = (_, ids) => ids.Contains("a01")
            ? throw GmailRetryPolicy.CreateApiException(HttpStatusCode.NotFound, "notFound")
            : Task.CompletedTask;
        var batch = await ApplyAsync(new ApplyRequest());

        await h.RunNextAsync();

        (await JobAsync(batch)).Status.ShouldBe(JobStatus.Completed);
        h.Progress(batch.JobId!.Value)[^1].Message.ShouldBe("Applied 2 of 2 messages; 1 skipped (not found in Gmail)");
        Labels("a00").ShouldNotContain("INBOX");
        Labels("a02").ShouldNotContain("INBOX");
        await using var db = postgres.CreateDbContext();
        (await db.ActionLog.Where(l => l.BatchId == batch.Id).Select(l => l.MessageId).ToListAsync(Ct)).ShouldBe(["a00", "a02"], ignoreOrder: true);
        (await db.Messages.SingleAsync(m => m.Id == "a01", Ct)).DeletedInGmail.ShouldBeTrue();
        (await db.Suggestions.SingleAsync(s => s.MessageId == "a01", Ct)).Status.ShouldBe(SuggestionStatus.Approved);
        (await db.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct)).MessageCount.ShouldBe(2);
        (await db.Senders.SingleAsync(s => s.Address == AnalysisRunHarness.Shop, Ct)).AppliedCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_chunk_whose_messages_are_all_gone_is_skipped_and_the_job_completes()
    {
        await SeedAsync(("a00", "Example", false, false, SuggestionStatus.Approved), ("a01", "Example", false, false, SuggestionStatus.Approved));
        h.Gmail.BeforeBatchModify = (_, _) => throw GmailRetryPolicy.CreateApiException(HttpStatusCode.NotFound, "notFound");
        var batch = await ApplyAsync(new ApplyRequest());

        await h.RunNextAsync();

        await AllGoneAsync(batch);
    }

    [Fact]
    public async Task A_resent_chunk_whose_messages_are_all_gone_is_cleared_and_the_job_completes()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync(("a00", "Example", false, false, SuggestionStatus.Approved), ("a01", "Example", false, false, SuggestionStatus.Approved));
        using var stop = new CancellationTokenSource();
        h.Gmail.BeforeBatchModify = (call, _) => call == 1
            ? Stop(stop)
            : throw GmailRetryPolicy.CreateApiException(HttpStatusCode.NotFound, "notFound");
        var batch = await ApplyAsync(new ApplyRequest());
        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);

        await h.RunNextAsync();

        await AllGoneAsync(batch);
        JsonSerializer.Deserialize<ApplyCursor>((await JobAsync(batch)).Cursor!, JsonSerializerOptions.Web)!.Pending.ShouldBeNull();
        (await RefusesConnectAsync()).ShouldBeFalse("the apply finished");
    }

    /// <summary>a00 and a01 were skipped as gone: marked deleted in Gmail, unlogged, still approved, and the job completed.</summary>
    private async Task AllGoneAsync(ActionBatchDto batch)
    {
        (await JobAsync(batch)).Status.ShouldBe(JobStatus.Completed);
        h.Progress(batch.JobId!.Value)[^1].Message!.ShouldContain("2 skipped (not found in Gmail)");
        await using var db = postgres.CreateDbContext();
        (await db.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(0);
        (await db.Messages.CountAsync(m => (m.Id == "a00" || m.Id == "a01") && m.DeletedInGmail, Ct)).ShouldBe(2);
        (await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Approved, Ct)).ShouldBe(2);
        (await db.ActionBatches.SingleAsync(b => b.Id == batch.Id, Ct)).MessageCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_message_starred_between_chunks_never_gets_the_delete_label()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 1;
        await SeedAsync(("a00", "Example", false, true, SuggestionStatus.Approved), ("a01", "Example", false, true, SuggestionStatus.Approved));
        string? starred = null;
        h.Gmail.BeforeBatchModify = (call, ids) => call == 1 ? StarAsync(starred = ids[0] == "a00" ? "a01" : "a00") : Task.CompletedTask;
        var batch = await ApplyAsync(new ApplyRequest());

        await h.RunNextAsync();

        var deleteId = (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == DeleteLabel).Id;
        Labels(starred!).ShouldNotContain(deleteId);
        Labels(starred!).ShouldContain("STARRED");
        await using var db = postgres.CreateDbContext();
        var log = await db.ActionLog.SingleAsync(l => l.BatchId == batch.Id && l.MessageId == starred, Ct);
        log.Note.ShouldBe("protected: starred");
        log.LabelsAdded.ShouldNotContain(DeleteLabel);
        (await JobAsync(batch)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task A_sender_allowlisted_after_review_never_gets_the_delete_label()
    {
        await SeedAsync(("a00", "Example", false, true, SuggestionStatus.Approved));
        (await h.PutAsync($"/api/senders/{AnalysisRunHarness.Shop}/allowlist", new AllowlistRequest(true))).StatusCode.ShouldBe(HttpStatusCode.OK);
        h.Gmail.CreateLabelCalls.ShouldBeEmpty();
        h.Gmail.BatchModifyCalls.ShouldBeEmpty();
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Suggestions.SingleAsync(s => s.MessageId == "a00", Ct)).Status.ShouldBe(SuggestionStatus.Approved);
        }

        var batch = await ApplyAsync(new ApplyRequest());
        await h.RunNextAsync();

        var deleteId = (await h.Gmail.Inner.ListLabelsAsync(Ct)).SingleOrDefault(l => l.Name == DeleteLabel)?.Id;
        if (deleteId is not null)
        {
            Labels("a00").ShouldNotContain(deleteId);
        }

        await using var check = postgres.CreateDbContext();
        var log = await check.ActionLog.SingleAsync(l => l.BatchId == batch.Id && l.MessageId == "a00", Ct);
        log.Note.ShouldBe("protected: allowlisted sender");
        log.LabelsAdded.ShouldNotContain(DeleteLabel);
    }

    private static Task Stop(CancellationTokenSource stop)
    {
        stop.Cancel();
        stop.Token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private async Task ResumeAsync(Guid jobId)
    {
        await using var scope = h.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IJobService>().ResumeAsync(jobId, Ct)).ShouldBe(JobActionResult.Ok);
    }

    private async Task<bool> RefusesConnectAsync()
    {
        await using var scope = h.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountGuard>().RefusesConnectAsync("other@example.com", Ct);
    }

    private async Task SeedAsync(params (string Id, string Topic, bool NeedsAction, bool ToBeDeleted, SuggestionStatus Status)[] rows)
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
            suggestion.SetStatus(row.Status, message, decidedAt);
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

    private IReadOnlyList<string> Labels(string id) => h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds;

    private async Task<ActionBatchDto> ApplyAsync(ApplyRequest request)
    {
        var response = await h.PostAsync("/api/review/apply", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var batch = (await response.Content.ReadFromJsonAsync<ActionBatchDto>(Ct)).ShouldNotBeNull();
        batch.JobId.ShouldNotBeNull();
        return batch;
    }

    private async Task<JobRow> JobAsync(ActionBatchDto batch)
    {
        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.SingleAsync(j => j.Id == batch.JobId, Ct);
        (job.Type, job.Queue, job.DedupKey).ShouldBe((ReviewJobTypes.Apply, JobQueues.Apply, batch.Id.ToString()));
        return job;
    }
}
