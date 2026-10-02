using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>A resent apply chunk that Gmail keeps refusing (#137).</summary>
public sealed partial class UndoActionsJobTests
{
    [Fact]
    public async Task A_resent_chunk_refused_three_times_is_reconciled_with_Gmail_and_can_be_cancelled_and_undone()
    {
        string[] chunk = [];
        var (apply, before, accept) = await RefuseResendsAsync(async pending =>
        {
            chunk = pending.MessageIds;
            // Gmail applied the chunk to its first message only, without answering.
            await h.Gmail.Inner.BatchModifyAsync([chunk[0]], pending.Add, pending.Remove, Ct);
        });
        var (changed, unchanged) = (chunk[0], chunk[1]);

        // The changed message stays applied; the unchanged one is approved again and its log row removed.
        await using (var db = postgres.CreateDbContext())
        {
            var stored = await db.ActionBatches.SingleAsync(b => b.Id == apply.Id, Ct);
            (stored.Description, stored.MessageCount, stored.SendFailures).ShouldBe(($"{apply.Description} (partial: 3 of 4)", 3, 0));
            (await db.ActionLog.Where(l => l.BatchId == apply.Id).Select(l => l.MessageId).OrderBy(id => id).ToListAsync(Ct))
                .ShouldBe(before.Keys.Where(id => id != unchanged).Order());
            (await db.Suggestions.SingleAsync(s => s.MessageId == unchanged, Ct)).Status.ShouldBe(SuggestionStatus.Approved);
            (await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Applied, Ct)).ShouldBe(3);
            (await db.Senders.SumAsync(s => s.AppliedCount, Ct)).ShouldBe(3);
            await StoredLabelsMatchGmailAsync(db, 4);
        }

        Labels(changed).ShouldNotContain("INBOX");
        Labels(unchanged).ShouldContain("INBOX");
        await using (var scope = h.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IAccountGuard>().RefusesConnectAsync("other@example.com", Ct)).ShouldBeFalse();
        }

        (await h.PostAsync($"/api/jobs/{apply.JobId}/cancel", new { })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ApplyJobAsync(apply)).Status.ShouldBe(JobStatus.Cancelled);

        accept();
        var undo = await UndoAsync(apply.Id);
        await h.RunNextAsync();

        (await JobAsync(undo)).Status.ShouldBe(JobStatus.Completed);
        foreach (var (id, labels) in before)
        {
            Labels(id).ShouldBe(labels, ignoreOrder: true);
        }

        await using var after = postgres.CreateDbContext();
        await StoredLabelsMatchGmailAsync(after, 4);
        (await after.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Approved, Ct)).ShouldBe(4);
        (await after.Senders.SumAsync(s => s.AppliedCount, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_message_gone_when_a_resent_chunk_is_finalised_is_skipped_and_marked_deleted_after_a_failed_re_read()
    {
        string[] chunk = [];
        var (apply, before, _) = await RefuseResendsAsync(pending =>
        {
            chunk = pending.MessageIds;
            h.Gmail.Inner.DeleteMessage(chunk[0]);
            var reads = h.Gmail.MetadataCalls.Count;
            h.Gmail.AfterMetadata = call => call == reads + 1
                ? throw GmailRetryPolicy.CreateApiException(HttpStatusCode.InternalServerError, "backendError")
                : Task.CompletedTask;
            return Task.CompletedTask;
        }, reReadFailures: 1);
        var gone = chunk[0];

        var cursor = JsonSerializer.Deserialize<ApplyCursor>((await ApplyJobAsync(apply)).Cursor!, JsonSerializerOptions.Web)!;
        cursor.Skipped.ShouldNotBeNull().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.MessageId.ShouldBe(gone), s => s.Reason.ShouldBe(ApplyActionsJob.NotFoundReason));
        await using var db = postgres.CreateDbContext();
        (await db.Messages.SingleAsync(m => m.Id == gone, Ct)).DeletedInGmail.ShouldBeTrue();
        (await db.ActionLog.Where(l => l.BatchId == apply.Id).Select(l => l.MessageId).OrderBy(id => id).ToListAsync(Ct))
            .ShouldBe(before.Keys.Except(chunk).Order());
        (await db.Suggestions.Where(s => s.Status == SuggestionStatus.Approved).Select(s => s.MessageId).OrderBy(id => id).ToListAsync(Ct))
            .ShouldBe(chunk.Order());
        (await db.ActionBatches.SingleAsync(b => b.Id == apply.Id, Ct)).MessageCount.ShouldBe(2);
        (await db.Senders.SumAsync(s => s.AppliedCount, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task A_resend_that_is_rate_limited_or_fails_on_the_server_is_never_counted()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync([.. Enumerable.Range(0, 4).Select(i => ($"a{i:D2}", "Example", false, false))]);
        using var stop = new CancellationTokenSource();
        h.Gmail.BeforeBatchModify = (call, _) => call switch
        {
            1 => Task.CompletedTask,
            2 => Stop(stop),
            _ => throw GmailRetryPolicy.CreateApiException(HttpStatusCode.InternalServerError, "backendError"),
        };
        var apply = (await (await h.PostAsync("/api/review/apply", new ApplyRequest())).Content.ReadFromJsonAsync<ActionBatchDto>(Ct)).ShouldNotBeNull();
        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);

        for (var attempt = 0; attempt <= ApplyActionsJob.MaxSendFailures; attempt++)
        {
            await h.RunNextAsync();
            var job = await ApplyJobAsync(apply);
            job.Status.ShouldBe(JobStatus.Failed);
            JsonSerializer.Deserialize<ApplyCursor>(job.Cursor!, JsonSerializerOptions.Web)!.Pending.ShouldNotBeNull();
            await using var scope = h.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<IJobService>().ResumeAsync(job.Id, Ct)).ShouldBe(JobActionResult.Ok);
        }

        await using var db = postgres.CreateDbContext();
        (await db.ActionBatches.SingleAsync(b => b.Id == apply.Id, Ct)).SendFailures.ShouldBe(0);
    }

    /// <summary>
    /// Applies a00..a03 in chunks of two, interrupts the second chunk and resends it until Gmail has refused it
    /// <see cref="ApplyActionsJob.MaxSendFailures"/> times; <paramref name="beforeLast"/> runs before that attempt.
    /// </summary>
    /// <param name="reReadFailures">Attempts whose re-read of the chunk fails, so they neither finalise nor count.</param>
    /// <returns>The batch, each message's labels before it, and an action that stops the refusals.</returns>
    private async Task<(ActionBatchDto Apply, Dictionary<string, string[]> Before, Action Accept)> RefuseResendsAsync(
        Func<ApplyChunk, Task> beforeLast, int reReadFailures = 0)
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        await SeedAsync([.. Enumerable.Range(0, 4).Select(i => ($"a{i:D2}", "Example", false, false))]);
        var before = Enumerable.Range(0, 4).Select(i => $"a{i:D2}").ToDictionary(id => id, id => (string[])[.. Labels(id)]);
        using var stop = new CancellationTokenSource();
        var refuse = true;
        h.Gmail.BeforeBatchModify = (call, _) => call switch
        {
            1 => Task.CompletedTask,
            2 => Stop(stop),
            _ when refuse => throw GmailRetryPolicy.CreateApiException(HttpStatusCode.BadRequest, "invalidArgument"),
            _ => Task.CompletedTask,
        };
        var response = await h.PostAsync("/api/review/apply", new ApplyRequest());
        var apply = (await response.Content.ReadFromJsonAsync<ActionBatchDto>(Ct)).ShouldNotBeNull();
        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);

        var attempts = ApplyActionsJob.MaxSendFailures + reReadFailures;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await h.RunNextAsync();
            var job = await ApplyJobAsync(apply);
            job.Status.ShouldBe(JobStatus.Failed);
            var pending = JsonSerializer.Deserialize<ApplyCursor>(job.Cursor!, JsonSerializerOptions.Web)!.Pending;
            if (attempt < attempts)
            {
                // Still pending: only a resume may finish it.
                pending.ShouldNotBeNull();
                await using var db = postgres.CreateDbContext();
                (await db.ActionBatches.SingleAsync(b => b.Id == apply.Id, Ct)).SendFailures.ShouldBe(Math.Min(attempt, ApplyActionsJob.MaxSendFailures - 1));
                if (attempt == ApplyActionsJob.MaxSendFailures - 1)
                {
                    await beforeLast(pending);
                }

                await using var scope = h.Services.CreateAsyncScope();
                (await scope.ServiceProvider.GetRequiredService<IJobService>().ResumeAsync(job.Id, Ct)).ShouldBe(JobActionResult.Ok);
            }
            else
            {
                pending.ShouldBeNull();
                job.Error.ShouldNotBeNull().ShouldContain("Gmail shows as changed");
            }
        }

        return (apply, before, () => refuse = false);
    }

    private async Task<JobRow> ApplyJobAsync(ActionBatchDto batch)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == batch.JobId, Ct);
    }
}
