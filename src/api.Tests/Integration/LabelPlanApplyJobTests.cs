using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Applies label plans over the harness mailbox (stored messages a00–x04) with synthetic labels.</summary>
[Collection(PostgresCollection.Name)]
public sealed class LabelPlanApplyJobTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Plans = "/api/rules/labels/plans";

    // Two messages per merge chunk, so a merge of a few messages takes several chunks.
    private readonly AnalysisRunHarness h = new(factory, postgres)
    {
        ConfigureServices = s => s.Configure<GmailOptions>(o => o.BatchModifyMaxIds = 2),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeGmailClient Gmail => h.Gmail.Inner;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.LabelPlans.ExecuteDeleteAsync(Ct);
            await db.Filters.ExecuteDeleteAsync(Ct);
            await db.ActionLog.ExecuteDeleteAsync(Ct);
            await db.ActionBatches.ExecuteDeleteAsync(Ct);
        }

        await h.InitializeAsync();
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Nest_renames_the_label_and_its_children_keeping_their_ids()
    {
        var flat = await LabelAsync("Synthetic-Bills");
        var child = await LabelAsync("Synthetic-Bills/Water");
        Tag("a00", flat.Id);
        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.Nest, flat, proposed: "Synthetic/Bills"));

        var jobId = await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        Name(flat.Id).ShouldBe("Synthetic/Bills");
        Name(child.Id).ShouldBe("Synthetic/Bills/Water");
        h.Gmail.RenameLabelCalls.Count.ShouldBe(2);
        Labels("a00").ShouldContain(flat.Id);
        var applied = await PlanAsync(plan.Id);
        applied.Status.ShouldBe(LabelPlanStatus.Applied);
        applied.JobId.ShouldBe(jobId);
        applied.Items.ShouldHaveSingleItem().Status.ShouldBe(LabelPlanItemStatus.Applied);
        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Completed);
        h.Progress(jobId).ShouldContain(p => p.Message == "Synthetic-Bills" && p.Total == 1);
    }

    [Fact]
    public async Task A_nest_repeated_after_the_parent_was_renamed_renames_only_the_children_left()
    {
        var flat = await LabelAsync("Synthetic-Notes");
        var renamed = await LabelAsync("Synthetic-Notes/Done");
        var left = await LabelAsync("Synthetic-Notes/Left");
        var item = Item(LabelPlanItemKind.Nest, flat, proposed: "Synthetic/Notes");
        // A run that died after renaming the parent and one child.
        Gmail.RenameLabel(flat.Id, "Synthetic/Notes");
        Gmail.RenameLabel(renamed.Id, "Synthetic/Notes/Done");
        var plan = await SeedPlanAsync(item);

        await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        h.Gmail.RenameLabelCalls.ShouldBe([(left.Id, "Synthetic/Notes/Left")]);
        (await PlanAsync(plan.Id)).Items.ShouldHaveSingleItem().Status.ShouldBe(LabelPlanItemStatus.Applied);
    }

    [Fact]
    public async Task A_rename_to_an_existing_name_fails_the_item_and_merges_nothing()
    {
        var flat = await LabelAsync("Synthetic-Topic");
        await LabelAsync("Synthetic/Topic");
        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.Nest, flat, proposed: "Synthetic/Topic"));

        await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        Name(flat.Id).ShouldBe("Synthetic-Topic");
        var item = (await PlanAsync(plan.Id)).Items.ShouldHaveSingleItem();
        item.Status.ShouldBe(LabelPlanItemStatus.Failed);
        item.Error.ShouldNotBeNullOrEmpty();
        (await PlanAsync(plan.Id)).Status.ShouldBe(LabelPlanStatus.Applied);
        h.Gmail.BatchModifyCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Merge_moves_every_message_retargets_filters_and_an_undo_restores_the_labels()
    {
        var source = await LabelAsync("Synthetic Receipt");
        var target = await LabelAsync("Synthetic Receipts");
        foreach (var id in new[] { "a00", "a01", "a02" })
        {
            Tag(id, source.Id);
        }

        Tag("a02", target.Id);
        // Mail the app never stored is merged and undone too.
        Gmail.AddMessage(new FakeMessage("z00", "t-z00", "unstored@example.com", "Synthetic", DateTimeOffset.UtcNow, ["INBOX", source.Id]));
        string[] ids = ["a00", "a01", "a02", "z00"];
        var before = ids.ToDictionary(id => id, Labels);
        await SeedFilterAsync("filter-merge", source.Id);
        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.NearDuplicate, source, target: target, filters: ["filter-merge"]));

        await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        foreach (var id in ids)
        {
            Labels(id).ShouldContain(target.Id);
            Labels(id).ShouldNotContain(source.Id);
        }

        Gmail.Messages.Single(m => m.Id == "a03").LabelIds.ShouldNotContain(target.Id);
        (await Gmail.ListLabelsAsync(Ct)).ShouldContain(l => l.Id == source.Id);
        h.Gmail.BatchModifyCalls.Count.ShouldBe(2);
        (await PlanAsync(plan.Id)).Items.ShouldHaveSingleItem().Status.ShouldBe(LabelPlanItemStatus.Applied);

        ActionBatchRow batch;
        await using (var db = postgres.CreateDbContext())
        {
            batch = await db.ActionBatches.AsNoTracking().SingleAsync(Ct);
            (batch.Kind, batch.Description, batch.MessageCount).ShouldBe((ActionKind.LabelMerge, "Merged label Synthetic Receipt into Synthetic Receipts", 4));
            (await db.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(4);
            (await db.Messages.SingleAsync(m => m.Id == "a00", Ct)).LabelIds.ShouldContain(target.Id);
            var original = await db.Filters.AsNoTracking().SingleAsync(f => f.Id == "filter-merge", Ct);
            (original.DeletedAt is not null, original.DeletedByApp).ShouldBe((true, true));
            var retargeted = await db.Filters.AsNoTracking().SingleAsync(f => f.RestoredFrom == "filter-merge", Ct);
            retargeted.ReadAction().AddLabelIds.ShouldBe([target.Id]);
            retargeted.DeletedAt.ShouldBeNull();
        }

        (await Gmail.ListFiltersAsync(Ct)).ShouldContain(f => f.Criteria.From == "list@example.com" && f.Action.AddLabelIds.SequenceEqual(new[] { target.Id }));

        var undo = await h.PostAsync($"/api/history/{batch.Id}/undo", new { });
        undo.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        foreach (var id in ids)
        {
            Labels(id).ShouldBe(before[id], ignoreOrder: true);
        }

        (await Gmail.ListLabelsAsync(Ct)).ShouldContain(l => l.Id == source.Id);
    }

    [Fact]
    public async Task A_merge_resumes_after_a_failure_between_chunks_without_logging_a_message_twice()
    {
        var source = await LabelAsync("Synthetic Old");
        var target = await LabelAsync("Synthetic New");
        string[] ids = ["b00", "b01", "b02", "b03", "b04"];
        foreach (var id in ids)
        {
            Tag(id, source.Id);
        }

        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.NearDuplicate, source, target: target));
        h.Gmail.BeforeBatchModify = (call, _) => call == 2
            ? throw new GmailRateLimitedException("Synthetic rate limit.")
            : Task.CompletedTask;

        var jobId = await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Failed);
        ids.Count(id => Labels(id).Contains(target.Id)).ShouldBe(2);
        (await PlanAsync(plan.Id)).Status.ShouldBe(LabelPlanStatus.Applying);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IJobService>().ResumeAsync(jobId, Ct)).ShouldBe(JobActionResult.Ok);
        }

        await h.RunNextAsync();

        ids.ShouldAllBe(id => Labels(id).Contains(target.Id) && !Labels(id).Contains(source.Id));
        await using var db = postgres.CreateDbContext();
        var batch = await db.ActionBatches.AsNoTracking().SingleAsync(Ct);
        batch.MessageCount.ShouldBe(5);
        var logged = await db.ActionLog.Where(l => l.BatchId == batch.Id).Select(l => l.MessageId).ToListAsync(Ct);
        logged.ShouldBe(ids, ignoreOrder: true);
        (await PlanAsync(plan.Id)).Status.ShouldBe(LabelPlanStatus.Applied);
    }

    [Fact]
    public async Task Delete_removes_an_empty_label_and_refuses_one_with_mail_or_filters()
    {
        var empty = await LabelAsync("Synthetic Empty");
        var full = await LabelAsync("Synthetic Full");
        var filtered = await LabelAsync("Synthetic Filtered");
        Tag("c00", full.Id);
        var plan = await SeedPlanAsync(
            Item(LabelPlanItemKind.Empty, empty), Item(LabelPlanItemKind.Empty, full),
            Item(LabelPlanItemKind.Empty, filtered, filters: ["filter-x"]));

        await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        var labels = await Gmail.ListLabelsAsync(Ct);
        labels.ShouldNotContain(l => l.Id == empty.Id);
        labels.ShouldContain(l => l.Id == full.Id);
        labels.ShouldContain(l => l.Id == filtered.Id);
        h.Gmail.DeleteLabelCalls.ShouldBe([empty.Id]);
        var items = (await PlanAsync(plan.Id)).Items.ToDictionary(i => i.LabelId);
        items[empty.Id].Status.ShouldBe(LabelPlanItemStatus.Applied);
        (items[full.Id].Status, items[full.Id].Error).ShouldBe((LabelPlanItemStatus.Failed, LabelPlanApplyJob.NotEmptyError));
        (items[filtered.Id].Status, items[filtered.Id].Error).ShouldBe((LabelPlanItemStatus.Failed, LabelPlanApplyJob.FilteredError));
    }

    [Fact]
    public async Task Items_run_nest_then_merge_then_delete_and_rejected_items_are_skipped()
    {
        var empty = await LabelAsync("Synthetic Spare");
        var source = await LabelAsync("Synthetic Dup");
        var target = await LabelAsync("Synthetic Dupe");
        var flat = await LabelAsync("Synthetic-Flat");
        var rejected = await LabelAsync("Synthetic Kept");
        Tag("x00", source.Id);
        var plan = await SeedPlanAsync(
            Item(LabelPlanItemKind.Empty, empty), Item(LabelPlanItemKind.NearDuplicate, source, target: target),
            Item(LabelPlanItemKind.Nest, flat, proposed: "Synthetic/Flat"),
            Item(LabelPlanItemKind.Empty, rejected, status: LabelPlanItemStatus.Rejected));

        var jobId = await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        h.Progress(jobId).Select(p => p.Message).Distinct().Take(3).ShouldBe(["Synthetic-Flat", "Synthetic Dup", "Synthetic Spare"]);
        h.Gmail.DeleteLabelCalls.ShouldBe([empty.Id]);
        (await PlanAsync(plan.Id)).Items.Single(i => i.LabelId == rejected.Id).Status.ShouldBe(LabelPlanItemStatus.Rejected);
    }

    [Fact]
    public async Task Cancel_keeps_the_applied_items_and_returns_the_plan_to_draft_for_the_rest()
    {
        var first = await LabelAsync("Synthetic-One");
        var second = await LabelAsync("Synthetic-Two");
        var plan = await SeedPlanAsync(
            Item(LabelPlanItemKind.Nest, first, proposed: "Synthetic/One"), Item(LabelPlanItemKind.Nest, second, proposed: "Synthetic/Two"));
        var jobId = await ApplyAsync(plan.Id);
        var cancelled = 0;
        h.OnPublish = async job =>
        {
            // The cancel publishes too: request it once.
            if (job.Id == jobId && job.Progress?.Message == "Synthetic-One" && Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                await using var scope = h.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IJobService>().CancelAsync(jobId, Ct);
            }
        };

        await h.RunNextAsync();

        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Cancelled);
        var draft = await PlanAsync(plan.Id);
        draft.Status.ShouldBe(LabelPlanStatus.Draft);
        draft.Items.Select(i => i.Status).ShouldBe([LabelPlanItemStatus.Applied, LabelPlanItemStatus.Accepted]);
        Name(second.Id).ShouldBe("Synthetic-Two");

        h.OnPublish = null;
        await ApplyAsync(plan.Id);
        await h.RunNextAsync();

        Name(second.Id).ShouldBe("Synthetic/Two");
        h.Gmail.RenameLabelCalls.Count.ShouldBe(2);
        (await PlanAsync(plan.Id)).Status.ShouldBe(LabelPlanStatus.Applied);
    }

    [Fact]
    public async Task Apply_is_409_unless_the_plan_is_a_draft_with_an_accepted_item()
    {
        var label = await LabelAsync("Synthetic Idle");
        var proposed = await SeedPlanAsync(Item(LabelPlanItemKind.Empty, label, status: LabelPlanItemStatus.Proposed));

        (await h.PostWithoutBodyAsync($"{Plans}/{proposed.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await h.PostWithoutBodyAsync($"{Plans}/{Guid.NewGuid()}/apply")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using (var db = postgres.CreateDbContext())
        {
            await db.LabelPlans.ExecuteDeleteAsync(Ct);
        }

        var plan = await SeedPlanAsync(Item(LabelPlanItemKind.Empty, label));
        await ApplyAsync(plan.Id);
        (await PlanAsync(plan.Id)).Status.ShouldBe(LabelPlanStatus.Applying);
        (await h.PostWithoutBodyAsync($"{Plans}/{plan.Id}/apply")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private async Task<GmailLabel> LabelAsync(string name) => await Gmail.CreateLabelAsync(name, Ct);

    private void Tag(string id, string labelId) => Gmail.SetLabels(id, [.. Labels(id), labelId]);

    private IReadOnlyList<string> Labels(string id) => Gmail.Messages.Single(m => m.Id == id).LabelIds;

    private string Name(string labelId) => Gmail.ListLabelsAsync(Ct).GetAwaiter().GetResult().Single(l => l.Id == labelId).Name;

    private static LabelPlanItem Item(
        LabelPlanItemKind kind, GmailLabel label, string? proposed = null, GmailLabel? target = null, string[]? filters = null,
        LabelPlanItemStatus status = LabelPlanItemStatus.Accepted) =>
        new(Guid.NewGuid(), kind, label.Id, label.Name, 0, proposed, target?.Id, target?.Name, filters ?? [], "Synthetic rationale.", status);

    private async Task<LabelPlanRow> SeedPlanAsync(params LabelPlanItem[] items)
    {
        var now = DateTimeOffset.UtcNow;
        var row = new LabelPlanRow { Id = Guid.CreateVersion7(), Status = LabelPlanStatus.Draft, LabelCount = items.Length, CreatedAt = now, UpdatedAt = now };
        row.WriteItems(items);
        await using var db = postgres.CreateDbContext();
        db.LabelPlans.Add(row);
        await db.SaveChangesAsync(Ct);
        return row;
    }

    private async Task SeedFilterAsync(string id, string labelId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = postgres.CreateDbContext();
        db.Filters.Add(new FilterRow
        {
            Id = id,
            Criteria = FilterRow.WriteCriteria(new GmailFilterCriteria(From: "list@example.com")),
            Action = FilterRow.WriteAction(new GmailFilterAction([labelId], ["INBOX"])),
            CriteriaSummary = "from:list@example.com",
            FirstSeenAt = now,
            LastSeenAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> ApplyAsync(Guid planId)
    {
        var response = await h.PostWithoutBodyAsync($"{Plans}/{planId}/apply");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<LabelPlanApplyDto>(Ct)).ShouldNotBeNull().JobId;
    }

    private async Task<LabelPlanDto> PlanAsync(Guid id) =>
        (await h.Host.CreateClient().GetFromJsonAsync<LabelPlanDto>($"{Plans}/{id}", Ct)).ShouldNotBeNull();

    private async Task<JobRow> JobAsync(Guid id)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
    }
}
