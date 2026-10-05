using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Replied-thread protection (#177) over the harness mailbox: every message is alone in thread <c>t-&lt;id&gt;</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RepliedThreadTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
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
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { DeleteLabelName = DeleteLabel }, Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task A_stored_sent_message_resolves_the_thread_without_asking_gmail()
    {
        await AddStoredAsync(Row("s00", "t-c00", MessageProtection.SentLabel));

        await CheckAsync("c00");

        h.Gmail.ThreadCalls.ShouldBeEmpty();
        (await RepliedAsync("c00", "s00")).ShouldBe([true, true]);
    }

    [Fact]
    public async Task Each_thread_is_asked_once_per_check_the_answer_is_stored_for_the_whole_thread_and_only_true_is_final()
    {
        h.Gmail.Inner.AddMessage(new FakeMessage(
            "s01", "t-c01", "User <user@example.com>", "Re: Invoice", DateTimeOffset.UtcNow, [MessageProtection.SentLabel]));
        await AddStoredAsync(Row("c00-older", "t-c00"), Row("gone", "t-gone"));

        await CheckAsync("c00", "c00-older", "c01", "c02", "gone");
        await CheckAsync("c00", "c01", "c02", "gone");

        h.Gmail.ThreadCalls.ShouldBe(["t-c00", "t-c01", "t-c02", "t-gone", "t-c00", "t-c02", "t-gone"], ignoreOrder: true);
        (await RepliedAsync("c00", "c00-older", "c01", "c02", "gone")).ShouldBe([false, false, true, false, false]);
    }

    [Fact]
    public async Task A_stored_not_replied_is_asked_again_and_a_reply_made_in_gmail_since_protects_the_thread()
    {
        await ApproveAsync(("c00", true));
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "c00").ExecuteUpdateAsync(s => s.SetProperty(m => m.ThreadReplied, false), Ct);
        }

        h.Gmail.Inner.AddMessage(new FakeMessage(
            "s00", "t-c00", "User <user@example.com>", "Re: Invoice", DateTimeOffset.UtcNow, [MessageProtection.SentLabel]));

        var batch = await ApplyAsync();
        await h.RunNextAsync();

        h.Gmail.ThreadCalls.ShouldBe(["t-c00"]);
        (await RepliedAsync("c00")).ShouldBe([true]);
        var deleteId = (await h.Gmail.Inner.ListLabelsAsync(Ct)).SingleOrDefault(l => l.Name == DeleteLabel)?.Id;
        if (deleteId is not null)
        {
            Labels("c00").ShouldNotContain(deleteId);
        }

        await using var after = postgres.CreateDbContext();
        (await after.ActionLog.SingleAsync(l => l.BatchId == batch.Id && l.MessageId == "c00", Ct)).Note
            .ShouldBe("protected: replied thread");
    }

    [Fact]
    public async Task A_new_message_reopens_not_replied_and_sent_makes_the_thread_replied_for_good()
    {
        await CheckAsync("c00");
        (await RepliedAsync("c00")).ShouldBe([false]);

        await UpsertAsync(Metadata("n00", "t-c00", "INBOX"));
        (await RepliedAsync("c00", "n00")).ShouldBe([null, null]);

        await UpsertAsync(Metadata("n01", "t-c00", MessageProtection.SentLabel));
        await UpsertAsync(Metadata("n02", "t-c00", "INBOX"));
        (await RepliedAsync("c00", "n00", "n01", "n02")).ShouldBe([true, true, true, true]);
        h.Gmail.ThreadCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Apply_withholds_the_delete_label_from_a_replied_thread_only()
    {
        h.Gmail.Inner.AddMessage(new FakeMessage(
            "s01", "t-c01", "User <user@example.com>", "Re: Invoice", DateTimeOffset.UtcNow, [MessageProtection.SentLabel]));
        await ApproveAsync(("c00", true), ("c01", true), ("c02", false));

        var batch = await ApplyAsync();
        await h.RunNextAsync();

        h.Gmail.ThreadCalls.ShouldBe(["t-c00", "t-c01"], ignoreOrder: true);
        var deleteId = (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == DeleteLabel).Id;
        Labels("c00").ShouldContain(deleteId);
        Labels("c01").ShouldNotContain(deleteId);
        await using var db = postgres.CreateDbContext();
        var log = await db.ActionLog.Where(l => l.BatchId == batch.Id).ToDictionaryAsync(l => l.MessageId, Ct);
        log["c01"].Note.ShouldBe("protected: replied thread");
        log["c01"].LabelsAdded.ShouldBe(["Billing"]);
        log["c00"].Note.ShouldBeNull();
        (await db.Jobs.SingleAsync(j => j.Id == batch.JobId, Ct)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task A_rate_limited_check_sends_nothing_and_the_resume_checks_again()
    {
        await ApproveAsync(("c00", true), ("c01", true));
        h.Gmail.BeforeThread = (threadId, _) => h.Gmail.ThreadCalls.Count == 2
            ? throw new GmailRateLimitedException("Synthetic rate limit.")
            : Task.CompletedTask;
        var batch = await ApplyAsync();

        await h.RunNextAsync();

        await using (var db = postgres.CreateDbContext())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == batch.JobId, Ct);
            (job.Status, job.Error).ShouldBe((JobStatus.Failed, "Synthetic rate limit."));
            job.Cursor.ShouldNotBeNull().ShouldNotContain("\"pending\":{");
            (await db.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(0);
        }

        h.Gmail.BatchModifyCalls.ShouldBeEmpty();
        var first = h.Gmail.ThreadCalls.First();
        h.Gmail.BeforeThread = null;
        await using (var scope = h.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IJobService>().ResumeAsync(batch.JobId!.Value, Ct)).ShouldBe(JobActionResult.Ok);
        }

        await h.RunNextAsync();

        // A stored "not replied" is only a hint, so the resume asks the first thread again.
        h.Gmail.ThreadCalls.Count(t => t == first).ShouldBe(2);
        (await RepliedAsync("c00", "c01")).ShouldBe([false, false]);
        await using var after = postgres.CreateDbContext();
        (await after.ActionLog.CountAsync(l => l.BatchId == batch.Id, Ct)).ShouldBe(2);
    }

    private async Task CheckAsync(params string[] ids)
    {
        await using var scope = h.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Messages.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<RepliedThreadChecker>().CheckAsync(rows, Ct);
    }

    private async Task UpsertAsync(GmailMessageMetadata message)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MessageUpserter>().UpsertAsync([message], Ct);
    }

    private async Task<bool?[]> RepliedAsync(params string[] ids)
    {
        await using var db = postgres.CreateDbContext();
        var stored = await db.Messages.Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.ThreadReplied, Ct);
        return [.. ids.Select(id => stored[id])];
    }

    private async Task AddStoredAsync(params MessageRow[] rows)
    {
        await using var db = postgres.CreateDbContext();
        db.Messages.AddRange(rows);
        await db.SaveChangesAsync(Ct);
    }

    private static MessageRow Row(string id, string threadId, params string[] labels) => new()
    {
        Id = id,
        ThreadId = threadId,
        FromAddress = "user@example.com",
        InternalDate = DateTimeOffset.UtcNow.AddDays(-1),
        LabelIds = labels,
        FetchedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GmailMessageMetadata Metadata(string id, string threadId, params string[] labels) =>
        new(id, threadId, "1", DateTimeOffset.UtcNow, labels, "user@example.com", null, "Synthetic", null, null, null, null, null, null, 100, false);

    private async Task ApproveAsync(params (string Id, bool ToBeDeleted)[] rows)
    {
        await using var db = postgres.CreateDbContext();
        var decidedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var (id, toBeDeleted) in rows)
        {
            var message = await db.Messages.SingleAsync(m => m.Id == id, Ct);
            var suggestion = new SuggestionRow
            {
                Id = Guid.NewGuid(),
                MessageId = id,
                SenderAddress = message.FromAddress,
                Source = SuggestionSource.Llm,
                TopicLabel = "Billing",
                ToBeDeleted = toBeDeleted,
                Confidence = 0.9,
                Reason = "Synthetic reason",
                CreatedAt = decidedAt,
            };
            suggestion.SetStatus(SuggestionStatus.Approved, message, decidedAt);
            db.Suggestions.Add(suggestion);
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task<ActionBatchDto> ApplyAsync()
    {
        var response = await h.PostAsync("/api/review/apply", new ApplyRequest());
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var batch = (await response.Content.ReadFromJsonAsync<ActionBatchDto>(Ct)).ShouldNotBeNull();
        batch.JobId.ShouldNotBeNull();
        return batch;
    }

    private IReadOnlyList<string> Labels(string id) => h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds;
}
