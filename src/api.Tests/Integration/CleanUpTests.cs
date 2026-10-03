using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.CleanUp;
using GmailOrganiser.Common;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The Clean-up list, Delete (to Trash), unmark and undo over the harness mailbox (every message starts in INBOX):
/// a01 has an attachment and b01 is starred, so both are protected.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CleanUpTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string DeleteLabel = "Synthetic Delete";
    private const string Shop = AnalysisRunHarness.Shop;
    private const string News = AnalysisRunHarness.News;
    private static readonly string[] Marked = ["a00", "a01", "a02", "a03", "b00", "b01"];

    private readonly AnalysisRunHarness h = new(factory, postgres);

    private string deleteLabelId = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ActionLog.ExecuteDeleteAsync();
            await db.ActionBatches.ExecuteDeleteAsync();
        }

        await h.InitializeAsync();
        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { DeleteLabelName = DeleteLabel }, Ct);
        }

        deleteLabelId = (await h.Gmail.Inner.CreateLabelAsync(DeleteLabel, Ct)).Id;
        foreach (var id in Marked)
        {
            await SetLabelsAsync(id, [.. Labels(id), deleteLabelId]);
        }

        await SetLabelsAsync("b01", [.. Labels("b01"), "STARRED"]);
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "a01").ExecuteUpdateAsync(s => s.SetProperty(m => m.HasAttachment, true), Ct);
        }

        // Already in Trash: never listed.
        await SetLabelsAsync("x00", [deleteLabelId, "TRASH"], deleted: true);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task The_list_groups_the_delete_labelled_mail_by_sender_with_protection()
    {
        (await GetAsync<CleanupSummaryDto>("/api/clean-up/summary")).ShouldBe(new CleanupSummaryDto(6, 2, 2));

        var senders = await GetAsync<PagedDto<CleanupSenderDto>>("/api/clean-up/senders?pageSize=1");
        (senders.Total, senders.Items.Count).ShouldBe((2, 1));
        var shop = senders.Items[0];
        (shop.Address, shop.Count, shop.ProtectedCount, shop.Allowlisted).ShouldBe((Shop, 4, 1, false));
        (shop.OldestAt > shop.NewestAt).ShouldBeFalse();
        (await GetAsync<PagedDto<CleanupSenderDto>>("/api/clean-up/senders?search=NEWS")).Items.Single().Count.ShouldBe(2);

        var messages = await GetAsync<PagedDto<CleanupMessageDto>>($"/api/clean-up/senders/{Shop}/messages");
        messages.Items.Select(m => m.Id).ShouldBe(["a00", "a01", "a02", "a03"]);
        messages.Items.Select(m => m.ProtectedReason).ShouldBe([null, "attachment", null, null]);
        messages.Items.ShouldAllBe(m => m.InInbox);

        (await h.GetAsync("/api/clean-up/senders/not-an-address/messages")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Without_the_delete_label_in_gmail_the_list_is_empty()
    {
        h.Gmail.Inner.DeleteLabel(deleteLabelId);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<LabelCatalog>().Invalidate();
        }

        (await GetAsync<CleanupSummaryDto>("/api/clean-up/summary")).ShouldBe(new CleanupSummaryDto(0, 0, 0));
        (await GetAsync<PagedDto<CleanupSenderDto>>("/api/clean-up/senders")).Total.ShouldBe(0);
        (await h.PostAsync("/api/clean-up/delete", new CleanupSelectionRequest(All: true))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await h.Gmail.Inner.ListLabelsAsync(Ct)).ShouldNotContain(l => l.Name == DeleteLabel);
    }

    [Fact]
    public async Task Delete_of_a_sender_trashes_its_unprotected_mail_and_logs_it()
    {
        var started = await StartAsync("delete", new CleanupSelectionRequest(SenderAddress: Shop.ToUpperInvariant()));
        (started.Queued, started.SkippedProtected).ShouldBe((3, 1));
        (started.Batch.Kind, started.Batch.Description).ShouldBe(("trash", $"Delete: 3 messages from {Shop}"));
        await h.RunNextAsync();

        string[] trashed = ["a00", "a02", "a03"];
        foreach (var id in trashed)
        {
            Labels(id).ShouldBe(["CATEGORY_UPDATES", deleteLabelId, "TRASH"], ignoreOrder: true);
        }

        Labels("a01").ShouldContain("INBOX");
        await using var db = postgres.CreateDbContext();
        var stored = await db.Messages.AsNoTracking().Where(m => trashed.Contains(m.Id)).ToListAsync(Ct);
        stored.ShouldAllBe(m => m.DeletedInGmail && m.LabelIds.Contains("TRASH") && !m.LabelIds.Contains("INBOX"));
        var log = await db.ActionLog.AsNoTracking().Where(l => l.BatchId == started.Batch.Id).ToListAsync(Ct);
        log.Select(l => l.MessageId).ShouldBe(trashed, ignoreOrder: true);
        log.ShouldAllBe(l => l.LabelIdsBefore.Contains("INBOX") && l.LabelIdsAfter.Contains("TRASH") && l.LabelsAdded.SequenceEqual(new[] { "TRASH" }));
        (await db.Senders.SingleAsync(s => s.Address == Shop, Ct)).TotalCount.ShouldBe(7);
        (await db.ActionBatches.SingleAsync(b => b.Id == started.Batch.Id, Ct)).MessageCount.ShouldBe(3);
        (await JobAsync(started)).Status.ShouldBe(JobStatus.Completed);
        h.Progress(started.Batch.JobId!.Value)[^1].ShouldBe(new JobProgress(3, 3, "Moved to Trash 3 of 3 messages; skipped 1 protected"));
        (await GetAsync<CleanupSummaryDto>("/api/clean-up/summary")).ShouldBe(new CleanupSummaryDto(3, 2, 2));
    }

    [Fact]
    public async Task Delete_all_with_include_protected_trashes_everything_listed()
    {
        var started = await StartAsync("delete", new CleanupSelectionRequest(All: true, IncludeProtected: true));
        (started.Queued, started.SkippedProtected, started.Batch.Description).ShouldBe((6, 0, "Delete: 6 messages"));
        await h.RunNextAsync();

        Marked.ShouldAllBe(id => Labels(id).Contains("TRASH") && !Labels(id).Contains("INBOX"));
        (await GetAsync<CleanupSummaryDto>("/api/clean-up/summary")).ShouldBe(new CleanupSummaryDto(0, 0, 0));
    }

    [Fact]
    public async Task A_message_protected_after_the_click_is_skipped_by_the_job()
    {
        var started = await StartAsync("delete", new CleanupSelectionRequest(MessageIds: ["a00", "a02"]));
        started.Queued.ShouldBe(2);
        await SetLabelsAsync("a00", [.. Labels("a00"), "STARRED"]);
        await h.RunNextAsync();

        Labels("a00").ShouldNotContain("TRASH");
        Labels("a02").ShouldContain("TRASH");
        await using var db = postgres.CreateDbContext();
        (await db.ActionBatches.SingleAsync(b => b.Id == started.Batch.Id, Ct)).MessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task Unmark_removes_the_delete_label_from_the_selection_only()
    {
        // x01 does not carry the label: it is not part of the selection.
        var started = await StartAsync("unmark", new CleanupSelectionRequest(MessageIds: ["a01", "b00", "x01"]));
        (started.Queued, started.SkippedProtected).ShouldBe((2, 0));
        (started.Batch.Kind, started.Batch.Description).ShouldBe(("unmark", "Removed from clean-up: 2 messages"));
        await h.RunNextAsync();

        Labels("a01").ShouldBe(["INBOX", "CATEGORY_UPDATES"], ignoreOrder: true);
        Labels("b00").ShouldNotContain(deleteLabelId);
        Labels("a00").ShouldContain(deleteLabelId);
        await using var db = postgres.CreateDbContext();
        (await db.Messages.CountAsync(m => (m.Id == "a01" || m.Id == "b00") && (m.LabelIds.Contains(deleteLabelId) || m.DeletedInGmail), Ct)).ShouldBe(0);
        (await GetAsync<CleanupSummaryDto>("/api/clean-up/summary")).ShouldBe(new CleanupSummaryDto(4, 2, 1));
    }

    [Fact]
    public async Task Undo_of_a_delete_takes_the_mail_out_of_trash_and_back_into_the_list()
    {
        var before = Marked.ToDictionary(id => id, id => Labels(id).ToArray());
        var started = await StartAsync("delete", new CleanupSelectionRequest(All: true));
        await h.RunNextAsync();
        // An incremental fetch stores a message in Trash as not deleted; it stays off the list and undoable.
        await using (var fetched = postgres.CreateDbContext())
        {
            await fetched.Messages.Where(m => m.Id == "a00").ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, false), Ct);
        }

        (await GetAsync<CleanupSummaryDto>("/api/clean-up/summary")).ShouldBe(new CleanupSummaryDto(2, 2, 2));

        var undo = await h.PostAsync($"/api/history/{started.Batch.Id}/undo", new { });
        undo.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        foreach (var id in Marked)
        {
            Labels(id).ShouldBe(before[id], ignoreOrder: true);
        }

        await using var db = postgres.CreateDbContext();
        var stored = await db.Messages.AsNoTracking().Where(m => Marked.Contains(m.Id)).ToListAsync(Ct);
        stored.ShouldAllBe(m => !m.DeletedInGmail && !m.LabelIds.Contains("TRASH") && m.LabelIds.Contains("INBOX"));
        (await db.Senders.SingleAsync(s => s.Address == Shop, Ct)).TotalCount.ShouldBe(10);
        (await db.ActionBatches.SingleAsync(b => b.Id == started.Batch.Id, Ct)).UndoneAt.ShouldNotBeNull();
        (await GetAsync<CleanupSummaryDto>("/api/clean-up/summary")).ShouldBe(new CleanupSummaryDto(6, 2, 2));
    }

    [Fact]
    public async Task A_restart_mid_delete_resends_the_pending_chunk()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 2;
        using var stop = new CancellationTokenSource();
        h.Gmail.BeforeBatchModify = (call, _) =>
        {
            if (call != 2)
            {
                return Task.CompletedTask;
            }

            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var started = await StartAsync("delete", new CleanupSelectionRequest(All: true, IncludeProtected: true));

        await h.RunNextAsync(stop.Token);
        await using (var db = postgres.CreateDbContext())
        {
            var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == started.Batch.JobId, Ct);
            job.Cursor.ShouldNotBeNull().ShouldContain("\"pending\": {");
        }

        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        h.Gmail.BatchModifyCalls.ElementAt(1).ShouldBe(h.Gmail.BatchModifyCalls.ElementAt(2));
        Marked.ShouldAllBe(id => Labels(id).Contains("TRASH"));
        await using var after = postgres.CreateDbContext();
        (await after.ActionLog.CountAsync(l => l.BatchId == started.Batch.Id, Ct)).ShouldBe(6);
        (await after.ActionBatches.SingleAsync(b => b.Id == started.Batch.Id, Ct)).MessageCount.ShouldBe(6);
        (await JobAsync(started)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task A_refused_id_is_skipped_and_the_rest_is_trashed()
    {
        // Gmail answers 404 for a chunk naming a message it no longer has.
        h.Gmail.BeforeBatchModify = (_, ids) => ids.Contains("a02")
            ? throw GmailRetryPolicy.CreateApiException(HttpStatusCode.NotFound, "notFound")
            : Task.CompletedTask;
        var started = await StartAsync("delete", new CleanupSelectionRequest(SenderAddress: Shop));
        await h.RunNextAsync();

        Labels("a00").ShouldContain("TRASH");
        Labels("a03").ShouldContain("TRASH");
        await using var db = postgres.CreateDbContext();
        (await db.ActionLog.Where(l => l.BatchId == started.Batch.Id).Select(l => l.MessageId).ToListAsync(Ct)).ShouldBe(["a00", "a03"], ignoreOrder: true);
        (await db.Messages.SingleAsync(m => m.Id == "a02", Ct)).DeletedInGmail.ShouldBeTrue();
        (await JobAsync(started)).Status.ShouldBe(JobStatus.Completed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"all\":true,\"senderAddress\":\"shop@example.com\"}")]
    [InlineData("{\"messageIds\":[]}")]
    [InlineData("{\"senderAddress\":\"two@example.com, three@example.com\"}")]
    public async Task A_selection_that_is_not_exactly_one_valid_choice_is_400(string body)
    {
        var client = h.Host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        var response = await client.PostAsync("/api/clean-up/delete", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Nothing_qualifying_is_204()
    {
        (await h.PostAsync("/api/clean-up/unmark", new CleanupSelectionRequest(MessageIds: ["x01"]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await h.PostAsync("/api/clean-up/delete", new CleanupSelectionRequest(MessageIds: ["a01"]))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<CleanupBatchDto> StartAsync(string action, CleanupSelectionRequest request)
    {
        var response = await h.PostAsync($"/api/clean-up/{action}", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<CleanupBatchDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<JobRow> JobAsync(CleanupBatchDto started)
    {
        await using var db = postgres.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == started.Batch.JobId, Ct);
        (job.Type, job.Queue, job.DedupKey).ShouldBe((CleanUpJobTypes.Actions, JobQueues.Apply, started.Batch.Id.ToString()));
        return job;
    }

    private async Task<T> GetAsync<T>(string path)
        where T : class
    {
        var response = await h.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }

    /// <summary>Sets the labels in the fake mailbox and on the stored row, as a fetch would.</summary>
    private async Task SetLabelsAsync(string id, string[] labels, bool deleted = false)
    {
        h.Gmail.Inner.SetLabels(id, labels);
        await using var db = postgres.CreateDbContext();
        await db.Messages.Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, labels).SetProperty(m => m.DeletedInGmail, deleted), Ct);
    }

    private IReadOnlyList<string> Labels(string id) => h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds;
}
