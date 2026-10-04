using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Auto-archive (#116): apply needs-action suggestions, change labels in the fake Gmail, run the incremental fetch.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ActionDoneScannerTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string ActionLabel = "Synthetic Action";
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
        await using (var db = postgres.CreateDbContext())
        {
            await db.FetchState.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.MailboxPhase, MailboxPhase.Completed)
                .SetProperty(r => r.LastHistoryId, FakeMailboxSeed.HistoryId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        await SettingsAsync(on: true);
    }

    /// <summary>The harness leaves fetch_state as migrated, for the later classes in the collection.</summary>
    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Removing_the_action_label_archives_once_and_the_batch_can_be_undone()
    {
        await SeedAsync("a00", "a01", "a02", "a03");
        await SeedAsync(("b00", NeedsAction: false));
        await ApplyAsync();
        var action = await ActionLabelIdAsync();
        Labels("a00").ShouldContain(action);
        Labels("b00").ShouldNotContain("INBOX");

        RemoveLabel("a00", action);
        RemoveLabel("a01", action);
        h.Gmail.Inner.SetLabels("a01", [.. Labels("a01"), action]);
        h.Gmail.Inner.SetLabels("a02", [.. Labels("a02").Where(l => l != action), "STARRED"]);
        await FetchAsync();

        Labels("a00").ShouldNotContain("INBOX");
        Labels("a01").ShouldContain("INBOX");
        Labels("a02").ShouldNotContain("INBOX");
        Labels("a03").ShouldContain("INBOX");
        Guid batchId;
        await using (var db = postgres.CreateDbContext())
        {
            var batch = (await db.ActionBatches.Where(b => b.Kind == ActionKind.AutoArchive).ToListAsync(Ct)).ShouldHaveSingleItem();
            (batch.Description, batch.MessageCount).ShouldBe((ActionDoneScanner.Description, 2));
            batchId = batch.Id;
            var rows = await db.ActionLog.Where(l => l.BatchId == batch.Id).OrderBy(l => l.MessageId).ToListAsync(Ct);
            rows.Select(r => r.MessageId).ShouldBe(["a00", "a02"]);
            rows.ShouldAllBe(r => r.SuggestionId == null && r.LabelsRemoved.SequenceEqual(new[] { "INBOX" }));
            rows[0].LabelIdsBefore.ShouldContain("INBOX");
            rows[0].LabelIdsAfter.ShouldNotContain("INBOX");
            (await db.Messages.SingleAsync(m => m.Id == "a00", Ct)).LabelIds.ShouldNotContain("INBOX");
        }

        // The user moves it back: the next fetch reads it again but never archives it a second time.
        var calls = h.Gmail.BatchModifyCalls.Count;
        h.Gmail.Inner.SetLabels("a00", [.. Labels("a00"), "INBOX"]);
        await FetchAsync();
        h.Gmail.BatchModifyCalls.Count.ShouldBe(calls);
        Labels("a00").ShouldContain("INBOX");

        var undo = await h.PostAsync($"/api/history/{batchId}/undo", new { });
        undo.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();
        Labels("a02").ShouldContain("INBOX");
        await using (var db = postgres.CreateDbContext())
        {
            (await db.ActionLog.CountAsync(l => l.BatchId == batchId && l.UndoneByBatchId != null, Ct)).ShouldBe(2);
            (await db.Suggestions.CountAsync(s => s.Status == SuggestionStatus.Applied, Ct)).ShouldBe(5);
            (await db.Senders.SumAsync(s => s.AppliedCount, Ct)).ShouldBe(5);
        }

        // An undone auto-archive is not redone when the message is read again.
        h.Gmail.Inner.SetLabels("a02", [.. Labels("a02"), "CATEGORY_PERSONAL"]);
        await FetchAsync();
        Labels("a02").ShouldContain("INBOX");
    }

    [Fact]
    public async Task Setting_off_archives_nothing()
    {
        await SettingsAsync(on: false);
        await SeedAsync("a00");
        await ApplyAsync();
        RemoveLabel("a00", await ActionLabelIdAsync());
        var calls = h.Gmail.BatchModifyCalls.Count;

        await FetchAsync();

        h.Gmail.BatchModifyCalls.Count.ShouldBe(calls);
        Labels("a00").ShouldContain("INBOX");
        await using var db = postgres.CreateDbContext();
        (await db.ActionBatches.AnyAsync(b => b.Kind == ActionKind.AutoArchive, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_action_label_is_a_no_op()
    {
        // Applied without a Gmail action label: the label was never created.
        await SeedAsync("a00");
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Applied);
        }

        h.Gmail.Inner.SetLabels("a00", [.. Labels("a00"), "CATEGORY_PERSONAL"]);

        await FetchAsync();

        h.Gmail.BatchModifyCalls.ShouldBeEmpty();
        Labels("a00").ShouldContain("INBOX");
    }

    [Fact]
    public async Task A_failed_send_stays_pending_and_the_next_fetch_resends_it()
    {
        await SeedAsync("a00", "a01");
        await ApplyAsync();
        var action = await ActionLabelIdAsync();
        RemoveLabel("a00", action);
        RemoveLabel("a01", action);
        var first = h.Gmail.BatchModifyCalls.Count + 1;
        h.Gmail.BeforeBatchModify = (call, _) => call == first ? throw new HttpRequestException("Synthetic timeout.") : Task.CompletedTask;

        (await FetchAsync()).Status.ShouldBe("completed");
        await using (var db = postgres.CreateDbContext())
        {
            var pending = (await db.ActionBatches.Where(b => b.Kind == ActionKind.AutoArchive).ToListAsync(Ct)).ShouldHaveSingleItem();
            pending.MessageCount.ShouldBe(0);
            (await db.ActionLog.CountAsync(l => l.BatchId == pending.Id, Ct)).ShouldBe(2);
        }

        h.Gmail.BeforeBatchModify = null;
        (await FetchAsync()).Status.ShouldBe("completed");

        h.Gmail.BatchModifyCalls.Last().ShouldBe(["a00", "a01"]);
        Labels("a00").ShouldNotContain("INBOX");
        Labels("a01").ShouldNotContain("INBOX");
        await using (var db = postgres.CreateDbContext())
        {
            (await db.ActionBatches.SingleAsync(b => b.Kind == ActionKind.AutoArchive, Ct)).MessageCount.ShouldBe(2);
            (await db.Messages.Where(m => m.Id == "a00" || m.Id == "a01").ToListAsync(Ct)).ShouldAllBe(m => !m.LabelIds.Contains("INBOX"));
        }
    }

    [Fact]
    public async Task Resends_that_keep_failing_never_fail_the_fetch_and_the_third_finalises_the_batch_as_partial()
    {
        await SeedAsync("a00");
        await ApplyAsync();
        RemoveLabel("a00", await ActionLabelIdAsync());
        h.Gmail.BeforeBatchModify = (_, _) => throw new HttpRequestException("Synthetic timeout.");
        (await FetchAsync()).Status.ShouldBe("completed");

        h.Gmail.BeforeBatchModify = (_, _) => throw new GoogleApiException("gmail", "Synthetic refusal.") { HttpStatusCode = HttpStatusCode.Forbidden };
        for (var i = 1; i <= ActionDoneScanner.MaxSendFailures; i++)
        {
            (await FetchAsync()).Status.ShouldBe("completed");
            await using var db = postgres.CreateDbContext();
            var batch = await db.ActionBatches.SingleAsync(b => b.Kind == ActionKind.AutoArchive, Ct);
            batch.SendFailures.ShouldBe(i);
            (batch.MessageCount, batch.Description).ShouldBe(i < ActionDoneScanner.MaxSendFailures
                ? (0, ActionDoneScanner.Description)
                : (1, ActionDoneScanner.Description + ActionDoneScanner.PartialSuffix));
            (await db.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(1);
        }

        // Finalised: not re-sent again, and History can undo it.
        var calls = h.Gmail.BatchModifyCalls.Count;
        (await FetchAsync()).Status.ShouldBe("completed");
        h.Gmail.BatchModifyCalls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task A_message_of_an_apply_job_with_a_pending_chunk_is_skipped_until_the_job_is_finalised()
    {
        await SeedAsync("a00");
        await ApplyAsync();
        Guid jobId;
        await using (var db = postgres.CreateDbContext())
        {
            jobId = (await db.ActionBatches.SingleAsync(b => b.Kind == ActionKind.Apply, Ct)).JobId!.Value;
            var pending = """{"messageIds": ["a00"], "add": [], "remove": []}""";
            await db.Database.ExecuteSqlAsync(
                $"UPDATE jobs SET status = 'failed', cursor = cursor || jsonb_build_object('pending', {pending}::jsonb) WHERE id = {jobId}", Ct);
        }

        RemoveLabel("a00", await ActionLabelIdAsync());
        await FetchAsync();
        Labels("a00").ShouldContain("INBOX");
        await using (var db = postgres.CreateDbContext())
        {
            (await db.ActionBatches.AnyAsync(b => b.Kind == ActionKind.AutoArchive, Ct)).ShouldBeFalse();
            await db.Database.ExecuteSqlAsync($"UPDATE jobs SET status = 'completed', cursor = cursor - 'pending' WHERE id = {jobId}", Ct);
        }

        h.Gmail.Inner.SetLabels("a00", [.. Labels("a00"), "CATEGORY_PERSONAL"]);
        await FetchAsync();
        Labels("a00").ShouldNotContain("INBOX");
    }

    [Fact]
    public async Task A_label_list_failure_never_fails_the_fetch_and_archives_nothing()
    {
        await SeedAsync("a00");
        await ApplyAsync();
        RemoveLabel("a00", await ActionLabelIdAsync());
        h.Services.GetRequiredService<LabelCatalog>().Invalidate();
        h.Gmail.AfterListLabels = () => throw new GoogleApiException("gmail", "Synthetic 500.") { HttpStatusCode = HttpStatusCode.InternalServerError };
        var calls = h.Gmail.BatchModifyCalls.Count;

        try
        {
            (await FetchAsync()).Status.ShouldBe("completed");
        }
        finally
        {
            h.Gmail.AfterListLabels = null;
        }

        h.Gmail.BatchModifyCalls.Count.ShouldBe(calls);
        Labels("a00").ShouldContain("INBOX");
        await using var db = postgres.CreateDbContext();
        (await db.ActionBatches.AnyAsync(b => b.Kind == ActionKind.AutoArchive, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Renaming_the_action_label_setting_does_not_archive_older_to_dos()
    {
        await SeedAsync("a00");
        await ApplyAsync();
        await h.Gmail.CreateLabelAsync(ActionLabel + " Renamed", Ct);
        await SettingsAsync(on: true, ActionLabel + " Renamed");
        h.Services.GetRequiredService<LabelCatalog>().Invalidate();
        var calls = h.Gmail.BatchModifyCalls.Count;

        // a00 keeps the old action label and never had the new one.
        h.Gmail.Inner.SetLabels("a00", [.. Labels("a00"), "CATEGORY_PERSONAL"]);
        await FetchAsync();

        h.Gmail.BatchModifyCalls.Count.ShouldBe(calls);
        Labels("a00").ShouldContain("INBOX");
    }

    private async Task SettingsAsync(bool on, string actionLabel = ActionLabel)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
            .UpdateAsync(s => s with { ActionLabelName = actionLabel, AutoArchiveOnActionDone = on }, Ct);
    }

    private Task SeedAsync(params string[] ids) => SeedAsync([.. ids.Select(id => (id, true))]);

    private async Task SeedAsync(params (string Id, bool NeedsAction)[] rows)
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
                TopicLabel = "Example",
                NeedsAction = row.NeedsAction,
                Confidence = 0.9,
                Reason = "Synthetic reason",
                CreatedAt = decidedAt,
            };
            suggestion.SetStatus(SuggestionStatus.Approved, message, decidedAt);
            db.Suggestions.Add(suggestion);
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task ApplyAsync()
    {
        (await h.PostAsync("/api/review/apply", new ApplyRequest())).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();
    }

    private async Task<JobDto> FetchAsync()
    {
        await using var scope = h.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobService>();
        var (job, _) = await jobs.EnqueueAsync(IncrementalFetchJob.JobType, IncrementalFetchJob.Queue, null, Ct);
        await h.RunNextAsync();
        return (await jobs.GetAsync(job.Id, Ct)).ShouldNotBeNull();
    }

    private async Task<string> ActionLabelIdAsync() => (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == ActionLabel).Id;

    private void RemoveLabel(string id, string label) => h.Gmail.Inner.SetLabels(id, [.. Labels(id).Where(l => l != label)]);

    private IReadOnlyList<string> Labels(string id) => h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds;
}
