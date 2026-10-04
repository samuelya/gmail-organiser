using System.Net;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Rules.Labels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The review round on #286: stopping jobs, Spam and Trash, re-sent chunks, current names, pause between chunks.</summary>
public sealed partial class LabelPlanApplyJobTests
{
    [Fact]
    public async Task Apply_is_409_while_the_plans_previous_job_is_still_active_and_the_plan_stays_draft()
    {
        var label = await LabelAsync("Synthetic Stopping");
        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.Empty, label));
        await using (var scope = h.Services.CreateAsyncScope())
        {
            // A cancelled job that has not ended yet still holds the plan's dedup key.
            var (_, created) = await scope.ServiceProvider.GetRequiredService<IJobService>().EnqueueAsync(
                LabelPlanApplyJob.JobType, LabelPlanApplyJob.Queue, new LabelPlanApplyCursor(plan.Id, []), Ct, plan.Id.ToString());
            created.ShouldBeTrue();
        }

        (await h.PostWithoutBodyAsync($"{Plans}/{plan.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var draft = await PlanAsync(plan.Id);
        (draft.Status, draft.JobId).ShouldBe((LabelPlanStatus.Draft, null));
    }

    [Fact]
    public async Task Merge_moves_mail_in_spam_and_trash_too()
    {
        var source = await LabelAsync("Synthetic Binned");
        var target = await LabelAsync("Synthetic Bin");
        Gmail.AddMessage(new FakeMessage("z10", "t-z10", "trash@example.com", "Synthetic", DateTimeOffset.UtcNow, ["TRASH", source.Id]));
        Gmail.AddMessage(new FakeMessage("z11", "t-z11", "spam@example.com", "Synthetic", DateTimeOffset.UtcNow, ["SPAM", source.Id]));
        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.NearDuplicate, source, target: target));

        await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        foreach (var id in new[] { "z10", "z11" })
        {
            Labels(id).ShouldContain(target.Id);
            Labels(id).ShouldNotContain(source.Id);
        }

        (await Gmail.GetLabelMessagesTotalAsync(source.Id, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_resent_chunk_gmail_refuses_keeps_its_rows_counted_so_history_can_undo_them()
    {
        var source = await LabelAsync("Synthetic Resent");
        var target = await LabelAsync("Synthetic Resend");
        Tag("b00", source.Id);
        Tag("b01", source.Id);
        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.NearDuplicate, source, target: target));
        using var stop = new CancellationTokenSource();
        h.Gmail.BeforeBatchModify = (call, _) =>
        {
            if (call == 1)
            {
                // The process dies during the first send: whether Gmail got it is unknown.
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }

            throw GmailRetryPolicy.CreateApiException(HttpStatusCode.BadRequest, "invalidArgument");
        };

        await ApplyAsync(plan.Id);
        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        var item = (await PlanAsync(plan.Id)).Items.ShouldHaveSingleItem();
        item.Status.ShouldBe(LabelPlanItemStatus.Failed);
        await using var db = postgres.CreateDbContext();
        var batch = await db.ActionBatches.AsNoTracking().SingleAsync(b => b.Kind == ActionKind.LabelMerge, Ct);
        batch.MessageCount.ShouldBe(2);
        (await db.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task A_merge_into_a_label_nested_by_the_same_plan_logs_the_new_name_and_lists_the_renames()
    {
        var target = await LabelAsync("Synthetic-Merged");
        var source = await LabelAsync("Synthetic Mergd");
        Tag("x00", source.Id);
        var plan = await SeedPlanAsync(
            Item(LabelPlanItemKind.NearDuplicate, source, target: target), Item(LabelPlanItemKind.Nest, target, proposed: "Synthetic/Merged"));

        await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        await using var db = postgres.CreateDbContext();
        var merge = await db.ActionBatches.AsNoTracking().SingleAsync(b => b.Kind == ActionKind.LabelMerge, Ct);
        merge.Description.ShouldBe("Merged label Synthetic Mergd into Synthetic/Merged");
        var row = await db.ActionLog.AsNoTracking().SingleAsync(l => l.BatchId == merge.Id, Ct);
        row.LabelsAdded.ShouldBe(["Synthetic/Merged"]);
        row.LabelsRemoved.ShouldBe(["Synthetic Mergd"]);
        row.LabelIdsBefore.ShouldBe([source.Id]);
        var rename = await db.ActionBatches.AsNoTracking().SingleAsync(b => b.Kind == ActionKind.LabelPlan, Ct);
        (rename.Description, rename.MessageCount).ShouldBe(("Renamed label Synthetic-Merged to Synthetic/Merged", 0));
    }

    [Fact]
    public async Task A_cancel_between_merge_chunks_stops_before_the_next_chunk_is_sent()
    {
        var source = await LabelAsync("Synthetic Halt");
        var target = await LabelAsync("Synthetic Halted");
        string[] ids = ["b00", "b01", "b02", "b03"];
        foreach (var id in ids)
        {
            Tag(id, source.Id);
        }

        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.NearDuplicate, source, target: target));
        var jobId = await ApplyAsync(plan.Id);
        var cancelled = 0;
        h.OnPublish = async job =>
        {
            // The first chunk's send is stored; the cancel comes before the second chunk is prepared.
            if (job.Id == jobId && h.Gmail.BatchModifyCalls.Count == 1 && Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                await using var scope = h.Services.CreateAsyncScope();
                (await scope.ServiceProvider.GetRequiredService<IJobService>().CancelAsync(jobId, Ct)).ShouldBe(JobActionResult.Ok);
            }
        };

        await h.RunNextAsync();

        h.Gmail.BatchModifyCalls.Count.ShouldBe(1);
        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Cancelled);
        (await PlanAsync(plan.Id)).Status.ShouldBe(LabelPlanStatus.Draft);
        ids.Count(id => Labels(id).Contains(target.Id)).ShouldBe(2);
        await using var db = postgres.CreateDbContext();
        var batch = await db.ActionBatches.AsNoTracking().SingleAsync(Ct);
        batch.MessageCount.ShouldBe(2);
        (await db.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(2);
    }
}
