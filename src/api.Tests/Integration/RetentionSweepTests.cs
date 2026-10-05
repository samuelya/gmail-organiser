using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.CleanUp;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The retention sweep (#368) over the harness mailbox (every message starts in INBOX, dated up to 70 hours before
/// 2026-03-01), thirty days later. Applied suggestions carry the mail types; a01 has an attachment, c00's subject is
/// transactional, a04 is starred, a07 is already archived and a08's sender has an approved keep policy.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetentionSweepTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string DeleteLabel = "Synthetic Delete";
    private static readonly DateTimeOffset Now = new(2026, 3, 31, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture postgres;
    private readonly FakeTimeProvider clock = new(Now);
    private readonly AnalysisRunHarness h;

    public RetentionSweepTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        h = new(factory, postgres)
        {
            ConfigureServices = services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(RetentionScheduler)));
            },
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.ActionLog.ExecuteDeleteAsync();
            await db.ActionBatches.ExecuteDeleteAsync();
            await db.SenderPolicies.ExecuteDeleteAsync();
        }

        await h.InitializeAsync();
        await UpdateSettingsAsync(s => s with { DeleteLabelName = DeleteLabel, Retention = s.Retention with { Enabled = true } });
        h.Gmail.Inner.SetLabels("a04", ["INBOX", "CATEGORY_UPDATES", MessageProtection.StarredLabel]);
        h.Gmail.Inner.SetLabels("a07", ["CATEGORY_UPDATES"]);

        await using (var db = postgres.CreateDbContext())
        {
            var keep = new SenderPolicyRow
            {
                Id = Guid.NewGuid(),
                Scope = PolicyScope.Sender,
                ScopeKey = AnalysisRunHarness.Other,
                TopicLabel = "Example/Other",
                Action = PolicyAction.Keep,
                Confidence = 0.9,
                Reason = "synthetic",
                Status = PolicyStatus.Approved,
                CreatedAt = Now,
            };
            var shortRetention = new SenderPolicyRow
            {
                Id = Guid.NewGuid(),
                Scope = PolicyScope.Sender,
                ScopeKey = AnalysisRunHarness.News,
                TopicLabel = "Example/News",
                Action = PolicyAction.Archive,
                RetentionDays = 10,
                Confidence = 0.9,
                Reason = "synthetic",
                Status = PolicyStatus.Approved,
                CreatedAt = Now,
            };
            db.SenderPolicies.AddRange(keep, shortRetention);

            var types = new Dictionary<string, (MailType Type, Guid? Policy)>
            {
                ["a00"] = (MailType.SecurityOtp, null),
                ["a01"] = (MailType.SecurityOtp, null),
                ["a02"] = (MailType.Receipt, null),
                ["a04"] = (MailType.SecurityOtp, null),
                ["a06"] = (MailType.Notification, null),
                ["a07"] = (MailType.SecurityOtp, null),
                ["b00"] = (MailType.Notification, shortRetention.Id),
                ["c00"] = (MailType.SecurityOtp, null),
                ["x00"] = (MailType.SecurityOtp, keep.Id),
            };
            foreach (var message in await db.Messages.Where(m => types.Keys.Contains(m.Id)).ToListAsync(Ct))
            {
                var suggestion = new SuggestionRow
                {
                    Id = Guid.NewGuid(),
                    MessageId = message.Id,
                    SenderAddress = message.FromAddress,
                    Source = SuggestionSource.Llm,
                    TopicLabel = "Example/Topic",
                    MailType = types[message.Id].Type,
                    PolicyId = types[message.Id].Policy,
                    Confidence = 0.9,
                    Reason = "synthetic",
                    CreatedAt = Now,
                };
                suggestion.SetStatus(SuggestionStatus.Applied, message, Now);
                db.Suggestions.Add(suggestion);
                message.HasAttachment = message.Id == "a01";
                message.LabelIds = [.. h.Gmail.Inner.Messages.Single(m => m.Id == message.Id).LabelIds];
            }

            // A message in a pending analysis is never swept, expired or not.
            var pending = await db.Messages.SingleAsync(m => m.Id == "a09", Ct);
            var analysed = new SuggestionRow
            {
                Id = Guid.NewGuid(),
                MessageId = "a09",
                SenderAddress = pending.FromAddress,
                Source = SuggestionSource.Llm,
                TopicLabel = "Example/Topic",
                MailType = MailType.SecurityOtp,
                Confidence = 0.9,
                Reason = "synthetic",
                CreatedAt = Now,
            };
            analysed.SetStatus(SuggestionStatus.Pending, pending, Now);
            db.Suggestions.Add(analysed);
            await db.SaveChangesAsync(Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await h.DisposeAsync();
        await using var db = postgres.CreateDbContext();
        await db.SenderPolicies.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Expired_mail_is_marked_and_archived_protected_and_kept_mail_is_not_and_undo_restores_it()
    {
        var before = h.Gmail.Inner.Messages.ToDictionary(m => m.Id, m => m.LabelIds.ToArray());
        // An upper bound: a01 (attachment), a04 (starred) and c00 (transactional) are skipped only by the sweep.
        (await StatusAsync()).EligibleNow.ShouldBe(6);

        var response = await h.PostWithoutBodyAsync("/api/clean-up/retention/run");
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = (await response.Content.ReadFromJsonAsync<JobDto>(Ct)).ShouldNotBeNull();
        (await h.PostWithoutBodyAsync("/api/clean-up/retention/run")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await h.RunNextAsync();

        var deleteLabelId = (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == DeleteLabel).Id;
        string[] marked = ["a00", "a07", "b00"];
        foreach (var id in marked)
        {
            Labels(id).ShouldContain(deleteLabelId);
            Labels(id).ShouldNotContain("INBOX");
        }

        foreach (var id in before.Keys.Except(marked))
        {
            Labels(id).ShouldBe(before[id], ignoreOrder: true);
        }

        h.Progress(job.Id).Last().Message.ShouldBe("Marked 3 of 3 messages; skipped 3 protected");
        var status = await StatusAsync();
        (status.Enabled, status.LastRunAt, status.LastMarked, status.NextDueAt, status.EligibleNow)
            .ShouldBe((true, Now, 3, Now + RetentionService.Interval, 3));

        await using (var db = postgres.CreateDbContext())
        {
            var batch = await db.ActionBatches.AsNoTracking().SingleAsync(Ct);
            (batch.Kind, batch.Description, batch.MessageCount, batch.JobId)
                .ShouldBe((ActionKind.Retention, $"Retention: 3 expired messages marked {DeleteLabel}", 3, job.Id));
            var log = await db.ActionLog.AsNoTracking().Where(l => l.BatchId == batch.Id).ToListAsync(Ct);
            log.Select(l => l.MessageId).ShouldBe(marked, ignoreOrder: true);
            log.Single(l => l.MessageId == "a07").LabelsRemoved.ShouldBeEmpty();
            (await db.Messages.AsNoTracking().Where(m => marked.Contains(m.Id)).ToListAsync(Ct))
                .ShouldAllBe(m => m.LabelIds.Contains(deleteLabelId) && !m.LabelIds.Contains("INBOX"));

            (await h.PostAsync($"/api/history/{batch.Id}/undo", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await h.RunNextAsync();
        foreach (var (id, labels) in before)
        {
            Labels(id).ShouldBe(labels, ignoreOrder: true);
        }
    }

    [Fact]
    public async Task A_sweep_that_starts_after_retention_was_turned_off_marks_nothing()
    {
        var before = h.Gmail.Inner.Messages.ToDictionary(m => m.Id, m => m.LabelIds.ToArray());
        (await h.PostWithoutBodyAsync("/api/clean-up/retention/run")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await UpdateSettingsAsync(s => s with { Retention = s.Retention with { Enabled = false } });
        await h.RunNextAsync();

        foreach (var (id, labels) in before)
        {
            Labels(id).ShouldBe(labels, ignoreOrder: true);
        }

        await using var db = postgres.CreateDbContext();
        (await db.ActionBatches.CountAsync(Ct)).ShouldBe(0);
        (await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == RetentionSweepJob.JobType, Ct)).Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task Mail_applied_before_its_senders_keep_policy_was_approved_is_not_swept()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "x00").ExecuteUpdateAsync(u => u.SetProperty(s => s.PolicyId, (Guid?)null), Ct);
            // As a fetch sets it; the harness seeds rows without it.
            await db.Messages.Where(m => m.Id == "x00").ExecuteUpdateAsync(u => u.SetProperty(m => m.CanonicalAddress, m => m.FromAddress), Ct);
        }

        var before = Labels("x00").ToArray();
        (await h.PostWithoutBodyAsync("/api/clean-up/retention/run")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();

        Labels("x00").ShouldBe(before, ignoreOrder: true);
        await using var check = postgres.CreateDbContext();
        (await check.ActionLog.AsNoTracking().Select(l => l.MessageId).ToListAsync(Ct)).ShouldBe(["a00", "a07", "b00"], ignoreOrder: true);
    }

    [Fact]
    public async Task Status_still_answers_when_the_delete_label_lookup_fails()
    {
        h.Gmail.AfterListLabels = () => throw new InvalidOperationException("synthetic label list failure");
        var status = await StatusAsync();
        (status.Enabled, status.LastRunAt, status.EligibleNow).ShouldBe((true, null, 6));
    }

    [Fact]
    public async Task The_scheduler_queues_one_sweep_a_day_and_none_while_retention_is_off()
    {
        await UpdateSettingsAsync(s => s with { Retention = s.Retention with { Enabled = false } });
        (await EnqueueIfDueAsync()).ShouldBeNull();
        (await h.PostWithoutBodyAsync("/api/clean-up/retention/run")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await StatusAsync()).NextDueAt.ShouldBeNull();

        await UpdateSettingsAsync(s => s with { Retention = s.Retention with { Enabled = true } });
        var first = (await EnqueueIfDueAsync()).ShouldNotBeNull();
        (await EnqueueIfDueAsync()).ShouldBeNull();
        await h.RunNextAsync();

        clock.Advance(RetentionService.Interval - TimeSpan.FromHours(1));
        (await EnqueueIfDueAsync()).ShouldBeNull();
        clock.Advance(TimeSpan.FromHours(1));
        var second = (await EnqueueIfDueAsync()).ShouldNotBeNull();
        second.Id.ShouldNotBe(first.Id);

        await using var db = postgres.CreateDbContext();
        (await db.Jobs.CountAsync(j => j.Type == RetentionSweepJob.JobType, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Retention_settings_show_keep_as_null_and_reject_unknown_types_and_out_of_range_days()
    {
        var bad = await h.PutAsync("/api/settings", new { retention = new { days = new Dictionary<string, int?> { ["receipt"] = 0, ["unknown"] = 5 } } });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = (await bad.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull();
        problem.Errors.Keys.ShouldBe(["retention.days.receipt", "retention.days.unknown"], ignoreOrder: true);

        var ok = await h.PutAsync("/api/settings", new { retention = new { days = new Dictionary<string, int?> { ["receipt"] = 365, ["security_otp"] = null } } });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var settings = (await ok.Content.ReadFromJsonAsync<SettingsDto>(Ct)).ShouldNotBeNull();
        settings.Retention.Enabled.ShouldBeTrue();
        settings.Retention.Days["receipt"].ShouldBe(365);
        settings.Retention.Days["security_otp"].ShouldBeNull();
        settings.Retention.Days["marketing"].ShouldBe(30);
        settings.Retention.Days["personal"].ShouldBeNull();
    }

    private async Task<JobDto?> EnqueueIfDueAsync()
    {
        await using var scope = h.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RetentionService>().EnqueueIfDueAsync(Ct);
    }

    private async Task UpdateSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(change, Ct);
    }

    private async Task<RetentionStatusDto> StatusAsync()
    {
        var response = await h.GetAsync("/api/clean-up/retention");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<RetentionStatusDto>(Ct)).ShouldNotBeNull();
    }

    private IReadOnlyList<string> Labels(string id) => h.Gmail.Inner.Messages.Single(m => m.Id == id).LabelIds;
}
