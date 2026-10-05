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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Stage-0 proposals and the sender archive (#349) over the harness mailbox (every message starts in INBOX): a01 has an
/// attachment (protected), a03 already carries the delete label, a05 was already analysed, and billing is a human sender.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Stage0Tests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string DeleteLabel = "Synthetic Delete";
    private const string Shop = AnalysisRunHarness.Shop;
    private const string News = AnalysisRunHarness.News;
    private const string Billing = AnalysisRunHarness.Billing;

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
        h.Gmail.Inner.SetLabels("a03", ["INBOX", "CATEGORY_UPDATES", deleteLabelId]);
        await using (var db = postgres.CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            await db.Messages.ExecuteUpdateAsync(s => s
                .SetProperty(m => m.CanonicalAddress, m => m.FromAddress)
                .SetProperty(m => m.CanonicalDomain, "example.com"), Ct);
            await db.Messages.Where(m => m.Id == "a01").ExecuteUpdateAsync(s => s.SetProperty(m => m.HasAttachment, true), Ct);
            await db.Messages.Where(m => m.Id == "a03")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", "CATEGORY_UPDATES", deleteLabelId }), Ct);
            await db.Messages.Where(m => m.Id == "a05")
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.AnalysisStatus, AnalysisStatus.Rejected), Ct);
            await db.Senders.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.CanonicalAddress, r => r.Address)
                .SetProperty(r => r.CanonicalDomain, r => r.Domain)
                .SetProperty(r => r.TotalCount, 10)
                .SetProperty(r => r.UnreadCount, 9)
                .SetProperty(r => r.Kind, SenderKind.Bulk)
                .SetProperty(r => r.StatsAt, now), Ct);
            await db.Senders.Where(s => s.Address == Billing).ExecuteUpdateAsync(s => s.SetProperty(r => r.Kind, SenderKind.Human), Ct);
        }
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Proposals_are_pending_stage0_suggestions_reviewed_and_bulk_approved_like_any_other()
    {
        var response = await h.PostAsync("/api/senders/noisy/proposals", new Stage0ProposalsRequest([Shop, News.ToUpperInvariant()], true, true));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<Stage0ProposalsResponse>(Ct)).ShouldBe(new Stage0ProposalsResponse(13, 1, 1));
        await using (var db = postgres.CreateDbContext())
        {
            var shop = await db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == Shop).OrderBy(s => s.MessageId).ToListAsync(Ct);
            shop.Select(s => s.MessageId).ShouldBe(["a00", "a02", "a04", "a06", "a07", "a08", "a09"]);
            shop.ShouldAllBe(s => s.Source == SuggestionSource.Stage0 && s.Status == SuggestionStatus.Pending
                && s.TopicLabel == DeleteLabel && !s.IsNewLabel && s.ToBeDeleted && s.UnsubscribeSuggested && s.Confidence == 1.0
                && s.Model == null && s.PromptVersion == Stage0Service.PromptVersion && s.GroupKey == "stage0:" + Shop);
            shop[0].Reason.ShouldBe("Stage 0: 10 messages, 90% unread, never replied, bulk headers");
            (await db.Messages.SingleAsync(m => m.Id == "a00", Ct)).AnalysisStatus.ShouldBe(AnalysisStatus.Analysed);
            (await db.Messages.SingleAsync(m => m.Id == "a05", Ct)).AnalysisStatus.ShouldBe(AnalysisStatus.Rejected);
            (await db.Senders.SingleAsync(s => s.Address == Shop, Ct)).AnalysedCount.ShouldBe(8);
        }

        var detail = (await h.Host.CreateClient().GetFromJsonAsync<ReviewSenderDetailDto>($"/api/review/senders/{Shop}", Ct)).ShouldNotBeNull();
        var group = detail.Groups.ShouldHaveSingleItem();
        (group.GroupKey, group.Size, group.Stage0Count, group.LlmCount, group.ToBeDeleted).ShouldBe(("stage0:" + Shop, 7, 7, 0, true));
        group.Members.ShouldAllBe(m => m.Source == "stage0");

        // Model answers only by default; Stage 0 comes with the derived ones.
        (await BulkApproveAsync(new BulkApproveRequest(null, SenderAddress: Shop))).Approved.ShouldBe(0);
        (await BulkApproveAsync(new BulkApproveRequest(null, IncludeDerived: true, SenderAddress: Shop))).Approved.ShouldBe(7);

        // Rule-made approvals teach no sender pattern and make no filter proposal.
        await using var scope = h.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<GmailOrganiser.Rules.FilterProposalQuery>().ListAsync(1, 50, Ct)).Total.ShouldBe(0);
    }

    [Fact]
    public async Task A_message_protected_before_apply_stays_in_the_inbox_and_no_decision_is_recorded()
    {
        (await h.PostAsync("/api/senders/noisy/proposals", new Stage0ProposalsRequest([Shop], true, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BulkApproveAsync(new BulkApproveRequest(null, IncludeDerived: true, SenderAddress: Shop))).Approved.ShouldBe(7);
        h.Gmail.Inner.SetLabels("a00", ["INBOX", "STARRED"]);
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "a00").ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", "STARRED" }), Ct);
        }

        (await h.PostAsync("/api/review/apply", new ApplyRequest(Shop))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        Labels("a00").ShouldBe(["INBOX", "STARRED"], ignoreOrder: true);
        Labels("a02").ShouldNotContain("INBOX");
        Labels("a02").ShouldContain(deleteLabelId);
        await using var after = postgres.CreateDbContext();
        (await after.Decisions.CountAsync(Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData(DeleteLabel)]
    [InlineData(" synthetic delete ")]
    public async Task Unticking_to_be_deleted_while_the_topic_stays_the_delete_label_is_400(string topic)
    {
        // #405: the edit is refused, so nothing is saved.
        (await h.PostAsync("/api/senders/noisy/proposals", new Stage0ProposalsRequest([Shop], true, false))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid id;
        await using (var db = postgres.CreateDbContext())
        {
            id = (await db.Suggestions.SingleAsync(s => s.MessageId == "a02", Ct)).Id;
        }

        var response = await h.PutAsync($"/api/review/suggestions/{id}", new EditSuggestionRequest(topic, false, false));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("topicLabel");
        await using var after = postgres.CreateDbContext();
        var stored = await after.Suggestions.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
        (stored.ToBeDeleted, stored.Edited, stored.Status).ShouldBe((true, false, SuggestionStatus.Pending));
    }

    [Fact]
    public async Task A_suggestion_not_to_be_deleted_under_the_delete_label_changes_nothing_on_apply()
    {
        // #405 past the endpoint: the planner itself never adds the delete label to mail not to be deleted.
        (await h.PostAsync("/api/senders/noisy/proposals", new Stage0ProposalsRequest([Shop], true, false))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BulkApproveAsync(new BulkApproveRequest(null, IncludeDerived: true, SenderAddress: Shop))).Approved.ShouldBe(7);
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a02")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ToBeDeleted, false).SetProperty(x => x.Edited, true), Ct);
        }

        var before = Labels("a02").ToList();
        (await h.PostAsync("/api/review/apply", new ApplyRequest(Shop))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        Labels("a02").ShouldBe(before, ignoreOrder: true);
        Labels("a02").ShouldContain("INBOX");
        Labels("a02").ShouldNotContain(deleteLabelId);
        Labels("a04").ShouldContain(deleteLabelId);
    }

    [Theory]
    [InlineData("/api/senders/noisy/proposals", 5, SenderKind.Bulk)]
    [InlineData("/api/senders/archive", 5, SenderKind.Bulk)]
    [InlineData("/api/senders/noisy/proposals", 10, SenderKind.Unknown)]
    [InlineData("/api/senders/archive", 10, SenderKind.Unknown)]
    public async Task A_sender_below_the_noisy_thresholds_or_without_bulk_headers_is_422(string path, int unread, SenderKind kind)
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Senders.Where(s => s.Address == News)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.UnreadCount, unread).SetProperty(r => r.Kind, kind), Ct);
        }

        var response = await h.PostAsync(path, new Stage0ProposalsRequest([News], true, false));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Detail.ShouldNotBeNull().ShouldContain(News);
    }

    [Theory]
    [InlineData("/api/senders/noisy/proposals")]
    [InlineData("/api/senders/archive")]
    public async Task A_sender_without_stats_is_422_naming_it(string path)
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Senders.Where(s => s.Address == News).ExecuteUpdateAsync(s => s.SetProperty(r => r.StatsAt, (DateTimeOffset?)null), Ct);
        }

        var response = await h.PostAsync(path, new Stage0ProposalsRequest([News], true, false));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Detail.ShouldNotBeNull().ShouldContain(News);
    }

    [Theory]
    [InlineData("/api/senders/noisy/proposals")]
    [InlineData("/api/senders/archive")]
    public async Task A_human_or_replied_to_sender_is_422_naming_it_and_nothing_happens(string path)
    {
        var response = await h.PostAsync(path, new Stage0ProposalsRequest([Shop, Billing], true, false));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Detail.ShouldNotBeNull().ShouldContain(Billing);

        await using (var db = postgres.CreateDbContext())
        {
            await db.Senders.Where(s => s.Address == News).ExecuteUpdateAsync(s => s.SetProperty(r => r.RepliedCount, 1), Ct);
        }

        var replied = await h.PostAsync(path, new Stage0ProposalsRequest([News], true, false));
        replied.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await replied.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Detail.ShouldNotBeNull().ShouldContain(News);

        await using var after = postgres.CreateDbContext();
        (await after.Suggestions.CountAsync(Ct)).ShouldBe(0);
        (await after.ActionBatches.CountAsync(Ct)).ShouldBe(0);
        (await after.Jobs.CountAsync(j => j.Type == SenderArchiveJob.JobType, Ct)).ShouldBe(0);
    }

    [Theory]
    [InlineData("{\"canonicalAddresses\":[],\"toBeDeleted\":true,\"unsubscribe\":true}")]
    [InlineData("{\"canonicalAddresses\":[\"not-an-address\"],\"toBeDeleted\":true,\"unsubscribe\":true}")]
    [InlineData("{\"canonicalAddresses\":[\"shop@example.com\"],\"toBeDeleted\":false,\"unsubscribe\":true}")]
    [InlineData("{\"canonicalAddresses\":[\"shop@example.com\"]}")]
    public async Task A_bad_proposals_request_is_400(string body)
    {
        var client = h.Host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        var response = await client.PostAsync(
            "/api/senders/noisy/proposals", new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Archive_removes_the_inbox_in_chunks_with_a_log_and_undo_restores_every_message()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 3;
        var before = h.Gmail.Inner.Messages.ToDictionary(m => m.Id, m => m.LabelIds.ToArray());

        var response = await h.PostAsync("/api/senders/archive", new SenderArchiveRequest([Shop]));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = (await response.Content.ReadFromJsonAsync<JobDto>(Ct)).ShouldNotBeNull();
        job.Type.ShouldBe(SenderArchiveJob.JobType);
        await h.RunNextAsync();

        string[] archived = ["a00", "a02", "a04", "a05", "a06", "a07", "a08", "a09"];
        foreach (var id in archived)
        {
            Labels(id).ShouldNotContain("INBOX");
        }

        Labels("a01").ShouldContain("INBOX");
        Labels("a03").ShouldContain("INBOX");
        Labels("b00").ShouldContain("INBOX");
        h.Gmail.BatchModifyCalls.Select(c => c.Count).ShouldBe([3, 3, 2]);
        h.Progress(job.Id).Last().Message.ShouldBe("Archived 8 of 8 messages; skipped 1 protected");

        await using (var db = postgres.CreateDbContext())
        {
            var batch = await db.ActionBatches.AsNoTracking().SingleAsync(Ct);
            (batch.Kind, batch.Description, batch.MessageCount, batch.JobId).ShouldBe((ActionKind.Archive, "Archive 8 messages from 1 sender", 8, job.Id));
            var log = await db.ActionLog.AsNoTracking().Where(l => l.BatchId == batch.Id).ToListAsync(Ct);
            log.Select(l => l.MessageId).ShouldBe(archived, ignoreOrder: true);
            log.ShouldAllBe(l => l.LabelsAdded.Length == 0 && l.LabelsRemoved.SequenceEqual(new[] { "INBOX" })
                && l.LabelIdsBefore.Contains("INBOX") && !l.LabelIdsAfter.Contains("INBOX"));
            (await db.Messages.AsNoTracking().Where(m => archived.Contains(m.Id)).ToListAsync(Ct)).ShouldAllBe(m => !m.LabelIds.Contains("INBOX"));
            (await db.Jobs.SingleAsync(j => j.Id == job.Id, Ct)).Status.ShouldBe(JobStatus.Completed);

            var undo = await h.PostAsync($"/api/history/{batch.Id}/undo", new { });
            undo.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await h.RunNextAsync();
        foreach (var (id, labels) in before)
        {
            Labels(id).ShouldBe(labels, ignoreOrder: true);
        }

        await using var after = postgres.CreateDbContext();
        (await after.Messages.AsNoTracking().Where(m => archived.Contains(m.Id)).ToListAsync(Ct)).ShouldAllBe(m => m.LabelIds.Contains("INBOX"));
    }

    [Fact]
    public async Task A_restart_mid_archive_resends_the_pending_chunk_once()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 3;
        using var stop = new CancellationTokenSource();
        h.Gmail.BeforeBatchModify = (call, _) => call == 2 ? Stop(stop) : Task.CompletedTask;
        var job = await StartArchiveAsync(Shop);

        await h.RunNextAsync(stop.Token);
        await using (var db = postgres.CreateDbContext())
        {
            var cursor = JsonSerializer.Deserialize<SenderArchiveCursor>((await db.Jobs.SingleAsync(j => j.Id == job.Id, Ct)).Cursor!, JsonSerializerOptions.Web);
            cursor.ShouldNotBeNull().Pending.ShouldNotBeNull().Length.ShouldBe(3);
        }

        h.Gmail.BeforeBatchModify = null;
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        h.Gmail.BatchModifyCalls.Select(c => c.Count).ShouldBe([3, 3, 3, 2]);
        h.Gmail.BatchModifyCalls.ElementAt(1).ShouldBe(h.Gmail.BatchModifyCalls.ElementAt(2));
        string[] archived = ["a00", "a02", "a04", "a05", "a06", "a07", "a08", "a09"];
        archived.ShouldAllBe(id => !Labels(id).Contains("INBOX"));
        await using var after = postgres.CreateDbContext();
        var done = await after.Jobs.SingleAsync(j => j.Id == job.Id, Ct);
        done.Status.ShouldBe(JobStatus.Completed);
        JsonSerializer.Deserialize<SenderArchiveCursor>(done.Cursor!, JsonSerializerOptions.Web).ShouldNotBeNull().Pending.ShouldBeNull();
        (await after.ActionLog.Select(l => l.MessageId).ToListAsync(Ct)).ShouldBe(archived, ignoreOrder: true);
        (await after.ActionBatches.SingleAsync(Ct)).MessageCount.ShouldBe(8);
    }

    [Fact]
    public async Task Archive_skips_mail_in_a_thread_the_user_replied_to_in_gmail()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 3;
        // Only Gmail knows the reply: the stored row has no SENT and no thread result yet.
        h.Gmail.Inner.SetLabels("a07", ["INBOX", "SENT"]);
        var job = await StartArchiveAsync(Shop);

        await h.RunNextAsync();

        Labels("a07").ShouldContain("INBOX");
        h.Gmail.BatchModifyCalls.Select(c => c.Count).ShouldBe([3, 3, 1]);
        h.Progress(job.Id).Last().Message.ShouldBe("Archived 7 of 7 messages; skipped 2 protected");
        await using var db = postgres.CreateDbContext();
        (await db.Messages.SingleAsync(m => m.Id == "a07", Ct)).ThreadReplied.ShouldBe(true);
        (await db.ActionLog.Select(l => l.MessageId).ToListAsync(Ct)).ShouldNotContain("a07");
    }

    [Fact]
    public async Task Archive_completes_early_when_the_sender_turns_human_and_history_can_undo_it()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 3;
        h.Gmail.BeforeBatchModify = async (call, _) =>
        {
            await using var db = postgres.CreateDbContext();
            await db.Senders.Where(s => s.Address == Shop).ExecuteUpdateAsync(s => s.SetProperty(r => r.RepliedCount, 1), Ct);
        };
        var job = await StartArchiveAsync(Shop);

        await h.RunNextAsync();

        h.Gmail.BatchModifyCalls.Count.ShouldBe(1);
        h.Progress(job.Id).Last().Message.ShouldNotBeNull().ShouldContain(Shop);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Jobs.SingleAsync(j => j.Id == job.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
            (await db.ActionLog.CountAsync(Ct)).ShouldBe(3);
            (await db.Messages.CountAsync(m => m.FromAddress == Shop && m.LabelIds.Contains("INBOX"), Ct)).ShouldBe(7);
            var batch = await db.ActionBatches.AsNoTracking().SingleAsync(Ct);
            h.Gmail.BeforeBatchModify = null;
            (await h.PostAsync($"/api/history/{batch.Id}/undo", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await h.RunNextAsync();
        await using var after = postgres.CreateDbContext();
        (await after.Messages.CountAsync(m => m.FromAddress == Shop && m.LabelIds.Contains("INBOX"), Ct)).ShouldBe(10);
    }

    [Fact]
    public async Task Archive_completes_early_when_an_address_of_the_sender_without_stats_appears()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 3;
        h.Gmail.BeforeBatchModify = async (_, _) =>
        {
            await using var db = postgres.CreateDbContext();
            if (!await db.Senders.AnyAsync(s => s.Address == "shop+new@example.com", Ct))
            {
                db.Senders.Add(new SenderRow
                {
                    Address = "shop+new@example.com",
                    Domain = "example.com",
                    CanonicalAddress = Shop,
                    CanonicalDomain = "example.com",
                    TotalCount = 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(Ct);
            }
        };
        var job = await StartArchiveAsync(Shop);

        await h.RunNextAsync();

        h.Gmail.BatchModifyCalls.Count.ShouldBe(1);
        h.Progress(job.Id).Last().Message.ShouldNotBeNull().ShouldContain("stopped");
        await using (var db = postgres.CreateDbContext())
        {
            (await db.Jobs.SingleAsync(j => j.Id == job.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
            (await db.ActionLog.CountAsync(Ct)).ShouldBe(3);
            var batch = await db.ActionBatches.AsNoTracking().SingleAsync(Ct);
            h.Gmail.BeforeBatchModify = null;
            (await h.PostAsync($"/api/history/{batch.Id}/undo", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await h.RunNextAsync();
        await using var after = postgres.CreateDbContext();
        (await after.Messages.CountAsync(m => m.FromAddress == Shop && m.LabelIds.Contains("INBOX"), Ct)).ShouldBe(10);
    }

    [Fact]
    public async Task Archive_goes_on_when_a_gone_message_drops_the_sender_below_the_noisy_thresholds()
    {
        h.Services.GetRequiredService<IOptions<GmailOptions>>().Value.BatchModifyMaxIds = 3;
        h.Gmail.BeforeBatchModify = (_, ids) => ids.Contains("a00")
            ? throw GmailRetryPolicy.CreateApiException(HttpStatusCode.NotFound, "notFound")
            : Task.CompletedTask;
        var job = await StartArchiveAsync(Shop);

        await h.RunNextAsync();

        string[] archived = ["a02", "a04", "a05", "a06", "a07", "a08", "a09"];
        archived.ShouldAllBe(id => !Labels(id).Contains("INBOX"));
        h.Progress(job.Id).Last().Message.ShouldNotBeNull().ShouldNotContain("stopped");
        await using var db = postgres.CreateDbContext();
        (await db.Senders.SingleAsync(s => s.Address == Shop, Ct)).TotalCount.ShouldBeLessThan(NoisySenderQuery.DefaultMinMessages);
        (await db.Jobs.SingleAsync(j => j.Id == job.Id, Ct)).Status.ShouldBe(JobStatus.Completed);
        (await db.ActionLog.Select(l => l.MessageId).ToListAsync(Ct)).ShouldBe(archived, ignoreOrder: true);
    }

    [Fact]
    public async Task Archive_with_nothing_unprotected_in_the_inbox_is_422()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.FromAddress == News)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "CATEGORY_UPDATES" }), Ct);
        }

        (await h.PostAsync("/api/senders/archive", new SenderArchiveRequest([News]))).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private async Task<BulkApproveResponse> BulkApproveAsync(BulkApproveRequest request)
    {
        var response = await h.PostAsync("/api/review/bulk-approve", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BulkApproveResponse>(Ct)).ShouldNotBeNull();
    }

    private async Task<JobDto> StartArchiveAsync(string sender)
    {
        var response = await h.PostAsync("/api/senders/archive", new SenderArchiveRequest([sender]));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<JobDto>(Ct)).ShouldNotBeNull();
    }

    private static Task Stop(CancellationTokenSource stop)
    {
        stop.Cancel();
        stop.Token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> Labels(string id) => h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds;
}
