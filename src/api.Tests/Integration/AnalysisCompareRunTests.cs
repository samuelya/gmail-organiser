using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Compare runs (#248): the current prompt over earlier suggestions, results stored as alternatives.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisCompareRunTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string HintOnlyForB00 = "Hint-For-B00";
    private const string OtherNewsHint = "Hint-For-Other-News";
    private readonly AnalysisRunHarness h = new(factory, postgres);
    private AnalysisRunDto first = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Three approved shop decisions make a memory pattern that short-circuits the shop group; a decision about b00
    /// itself must never reach a compare prompt. Then a normal run writes the 20 suggestions to compare.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await using (var db = postgres.CreateDbContext())
        {
            var at = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
            var scope = GroupKey.For(await db.Messages.AsNoTracking().SingleAsync(m => m.Id == "a00", Ct));
            db.Decisions.AddRange(Enumerable.Range(0, 3).Select(i => Decision($"seed-{i}", AnalysisRunHarness.Shop, scope, "Deals", at.AddHours(i))));
            db.Decisions.Add(Decision("b00", AnalysisRunHarness.News, null, HintOnlyForB00, at));
            db.Decisions.Add(Decision("seed-news", AnalysisRunHarness.News, null, OtherNewsHint, at));
            await db.SaveChangesAsync(Ct);
        }

        first = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        (await h.GetRunAsync(first.Id)).MessagesFromMemory.ShouldBe(10);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Compare_by_run_writes_alternatives_without_memory_and_leaves_suggestions_messages_and_senders_untouched()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a01", SuggestionStatus.Applied);
            await AnalysisRunHarness.DecideAsync(db, "c00", SuggestionStatus.Rejected);
        }

        var before = await SnapshotAsync();
        var calls = h.Chat.Calls;
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Answer(ids, (_, _) => "Compared"));

        var run = await StartCompareAsync(new CompareRunRequest(null, first.Id));
        (run.Kind, run.Scope, run.RequestedCount, run.SkippedMessages).ShouldBe(("compare", "messages", 20, 0));
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.MessagesFromMemory, done.LlmCalls).ShouldBe(("completed", 20, 0, 3));
        (h.Chat.Calls - calls).ShouldBe(3);
        (await SnapshotAsync()).ShouldBe(before);

        await using var check = postgres.CreateDbContext();
        var alternatives = await check.SuggestionAlternatives.AsNoTracking().ToListAsync(Ct);
        alternatives.Count.ShouldBe(20);
        alternatives.ShouldAllBe(a => a.RunId == run.Id && a.TopicLabel == "Compared" && a.Source != SuggestionSource.Memory);
        var suggestions = await check.Suggestions.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.MessageId, Ct);
        alternatives.ShouldAllBe(a => suggestions[a.SuggestionId] == a.MessageId);

        // Memory hints are part of the current prompt, but never a decision about a message being re-analysed.
        h.Chat.Requests.Take(calls).Single(r => r.Any(m => m.Text.Contains("id: b0")))
            .ShouldContain(m => m.Text.Contains(HintOnlyForB00));
        var news = h.Chat.Requests.Skip(calls).Single(r => r.Any(m => m.Text.Contains("id: b0")));
        news.ShouldNotContain(m => m.Text.Contains(HintOnlyForB00));
        news.ShouldContain(m => m.Text.Contains(OtherNewsHint));
    }

    [Fact]
    public async Task Compare_by_ids_skips_mail_deleted_in_gmail_and_unknown_ids_and_latest_run_wins()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "c01").ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true), Ct);
        }

        var ids = await SuggestionIdsAsync("c00", "c01", "c02");
        var run = await StartCompareAsync(new CompareRunRequest([.. ids, Guid.NewGuid()], null));
        (run.RequestedCount, run.SkippedMessages).ShouldBe((4, 2));
        await h.RunNextAsync();
        (await h.GetRunAsync(run.Id)).MessagesCovered.ShouldBe(2);

        h.Chat.Respond = (msgIds, _, _, _) => Task.FromResult(AnalysisRunHarness.Answer(msgIds, (_, _) => "Second"));
        var again = await StartCompareAsync(new CompareRunRequest([ids[0]], null));
        await h.RunNextAsync();

        await using var check = postgres.CreateDbContext();
        var alternatives = await check.SuggestionAlternatives.AsNoTracking().OrderBy(a => a.MessageId).ToListAsync(Ct);
        alternatives.Select(a => (a.MessageId, a.RunId, a.TopicLabel))
            .ShouldBe([("c00", again.Id, "Second"), ("c02", run.Id, "Finance")]);
    }

    [Fact]
    public async Task Restart_mid_compare_run_resumes_without_writing_twice()
    {
        using var stop = new CancellationTokenSource();
        var calls = h.Chat.Calls;
        h.Chat.Respond = (ids, call, _, ct) =>
        {
            if (call == calls + 2)
            {
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult(AnalysisRunHarness.Agree(ids));
        };
        var run = await StartCompareAsync(new CompareRunRequest(null, first.Id));

        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.LlmCalls).ShouldBe(("completed", 20, 3));
        await using var check = postgres.CreateDbContext();
        (await check.SuggestionAlternatives.CountAsync(a => a.RunId == run.Id, Ct)).ShouldBe(20);
    }

    [Fact]
    public async Task Suggestion_deleted_mid_run_is_skipped_not_failed()
    {
        var calls = h.Chat.Calls;
        h.Chat.Respond = async (ids, call, _, ct) =>
        {
            if (call == calls + 1)
            {
                // A normal re-analyse removes the billing suggestions while the compare run works.
                await using var db = postgres.CreateDbContext();
                await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Billing).ExecuteDeleteAsync(ct);
            }

            return AnalysisRunHarness.Agree(ids);
        };
        var run = await StartCompareAsync(new CompareRunRequest(null, first.Id));
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages, done.FailedMessages).ShouldBe(("completed", 16, 4, 0));
        await using var check = postgres.CreateDbContext();
        (await check.SuggestionAlternatives.CountAsync(Ct)).ShouldBe(16);
    }

    [Fact]
    public async Task Hints_leave_out_every_message_of_the_run_not_just_the_group()
    {
        await using (var db = postgres.CreateDbContext())
        {
            var at = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);
            foreach (var id in new[] { "c00", "c01", "c02" })
            {
                await AnalysisRunHarness.DecideAsync(db, id, SuggestionStatus.Applied);
                db.Decisions.Add(Decision(id, AnalysisRunHarness.Billing, null, $"Hint-For-{id}", at));
            }

            db.Decisions.Add(Decision("c03", AnalysisRunHarness.Billing, null, "Hint-Outside-Run", at));
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
                .UpdateAsync(x => x with { AnalysisGroupingMode = AnalysisGroupingMode.Off }, Ct);
        }

        var calls = h.Chat.Calls;
        var run = await StartCompareAsync(new CompareRunRequest(await SuggestionIdsAsync("c00", "c01", "c02"), null));
        await h.RunNextAsync();

        (await h.GetRunAsync(run.Id)).LlmCalls.ShouldBe(3);
        var prompts = h.Chat.Requests.Skip(calls).ToList();
        prompts.ShouldAllBe(r => !r.Any(m => m.Text.Contains("Hint-For-c0")));
        prompts.ShouldAllBe(r => r.Any(m => m.Text.Contains("Hint-Outside-Run")));
    }

    [Fact]
    public async Task Resume_does_not_redo_messages_whose_alternatives_another_run_replaced()
    {
        var (run, stored) = await CrashAfterFirstGroupAsync();
        await using (var db = postgres.CreateDbContext())
        {
            // Another compare run replaced them while this one was stopped.
            await db.SuggestionAlternatives.Where(a => a.RunId == run.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.RunId, first.Id).SetProperty(a => a.TopicLabel, "Newer"), Ct);
        }

        var calls = h.Chat.Calls;
        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages, done.LlmCalls).ShouldBe(("completed", 20, 0, 3));
        (h.Chat.Calls - calls).ShouldBe(2);
        await using var check = postgres.CreateDbContext();
        (await check.SuggestionAlternatives.CountAsync(a => a.TopicLabel == "Newer", Ct)).ShouldBe(stored.Length);
        (await check.SuggestionAlternatives.CountAsync(a => a.RunId == run.Id, Ct)).ShouldBe(20 - stored.Length);
    }

    [Fact]
    public async Task Resumed_older_run_keeps_a_later_created_runs_alternatives_and_replaces_older_ones()
    {
        var (run, stored) = await CrashAfterFirstGroupAsync();
        var created = (await h.GetRunAsync(run.Id)).CreatedAt;
        string[] rest;
        await using (var db = postgres.CreateDbContext())
        {
            // A compare run created after this one wrote the messages it has not reached yet; one of them came from an
            // earlier run instead.
            var newer = new AnalysisRunRow
            {
                Id = Guid.CreateVersion7(),
                Kind = AnalysisRunKind.Compare,
                Scope = AnalysisScope.Messages,
                Status = AnalysisRunStatus.Completed,
                CreatedAt = created.AddMinutes(1),
            };
            db.AnalysisRuns.Add(newer);
            var suggestions = await db.Suggestions.AsNoTracking().Where(s => !stored.Contains(s.MessageId)).OrderBy(s => s.MessageId).ToListAsync(Ct);
            rest = [.. suggestions.Select(s => s.MessageId)];
            db.SuggestionAlternatives.AddRange(suggestions.Select((s, i) =>
            {
                var older = i == 0;
                var alternative = SuggestionAlternativeRow.From(s, s.Id, older ? first.Id : newer.Id, created);
                alternative.TopicLabel = older ? "Older" : "Newer";
                return alternative;
            }));
            await db.SaveChangesAsync(Ct);
        }

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages).ShouldBe(("completed", stored.Length + 1, rest.Length - 1));
        await using var check = postgres.CreateDbContext();
        (await check.SuggestionAlternatives.CountAsync(a => a.TopicLabel == "Newer", Ct)).ShouldBe(rest.Length - 1);
        (await check.SuggestionAlternatives.SingleAsync(a => a.MessageId == rest[0], Ct)).RunId.ShouldBe(run.Id);
        (await check.SuggestionAlternatives.CountAsync(a => a.RunId == run.Id, Ct)).ShouldBe(stored.Length + 1);
    }

    [Fact]
    public async Task Resumed_older_run_does_not_write_over_an_outcome_accepted_from_a_later_created_run()
    {
        var (run, stored) = await CrashAfterFirstGroupAsync();
        var created = (await h.GetRunAsync(run.Id)).CreatedAt;
        string accepted;
        await using (var db = postgres.CreateDbContext())
        {
            // A later-created run's alternative was accepted into one message the resumed run has not reached yet: the
            // suggestion now carries that run's id and has no alternative left.
            var newer = new AnalysisRunRow
            {
                Id = Guid.CreateVersion7(),
                Kind = AnalysisRunKind.Compare,
                Scope = AnalysisScope.Messages,
                Status = AnalysisRunStatus.Completed,
                CreatedAt = created.AddMinutes(1),
            };
            db.AnalysisRuns.Add(newer);
            await db.SaveChangesAsync(Ct);
            accepted = await db.Suggestions.Where(s => !stored.Contains(s.MessageId)).OrderBy(s => s.MessageId).Select(s => s.MessageId).FirstAsync(Ct);
            await db.Suggestions.Where(s => s.MessageId == accepted).ExecuteUpdateAsync(s => s.SetProperty(x => x.RunId, newer.Id), Ct);
        }

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages).ShouldBe(("completed", 19, 1));
        await using var check = postgres.CreateDbContext();
        (await check.SuggestionAlternatives.AnyAsync(a => a.MessageId == accepted, Ct)).ShouldBeFalse();
        (await check.SuggestionAlternatives.CountAsync(a => a.RunId == run.Id, Ct)).ShouldBe(19);
    }

    [Fact]
    public async Task Resumed_older_run_does_not_write_over_an_alternative_discarded_from_a_later_created_run()
    {
        var (run, stored) = await CrashAfterFirstGroupAsync();
        var created = (await h.GetRunAsync(run.Id)).CreatedAt;
        string discarded;
        await using (var db = postgres.CreateDbContext())
        {
            // What the run wrote before stopping carries its marker; a later-created run then wrote one message it has
            // not reached yet, the same way, and the user discarded that alternative.
            (await db.Suggestions.Where(s => stored.Contains(s.MessageId)).Select(s => s.CompareRunCreatedAt).Distinct().ToListAsync(Ct))
                .ShouldBe([created]);
            var newer = new AnalysisRunRow
            {
                Id = Guid.CreateVersion7(),
                Kind = AnalysisRunKind.Compare,
                Scope = AnalysisScope.Messages,
                Status = AnalysisRunStatus.Completed,
                CreatedAt = created.AddMinutes(1),
            };
            db.AnalysisRuns.Add(newer);
            var suggestion = await db.Suggestions.Where(s => !stored.Contains(s.MessageId)).OrderBy(s => s.MessageId).FirstAsync(Ct);
            suggestion.CompareRunCreatedAt = newer.CreatedAt;
            db.SuggestionAlternatives.Add(SuggestionAlternativeRow.From(suggestion, suggestion.Id, newer.Id, created));
            await db.SaveChangesAsync(Ct);
            discarded = suggestion.MessageId;
        }

        var response = await h.PostAsync("/api/review/alternatives/discard", new AlternativeDecisionRequest(await SuggestionIdsAsync(discarded), null));
        (await response.Content.ReadFromJsonAsync<AlternativeDecisionResponse>(Ct)).ShouldBe(new AlternativeDecisionResponse(0, 1, 0));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages).ShouldBe(("completed", 19, 1));
        await using var check = postgres.CreateDbContext();
        (await check.SuggestionAlternatives.AnyAsync(a => a.MessageId == discarded, Ct)).ShouldBeFalse();
        (await check.SuggestionAlternatives.CountAsync(a => a.RunId == run.Id, Ct)).ShouldBe(19);
    }

    [Fact]
    public async Task Resume_does_not_count_messages_whose_alternatives_a_reanalyse_cascaded_away()
    {
        var (run, stored) = await CrashAfterFirstGroupAsync();
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => stored.Contains(s.MessageId)).ExecuteDeleteAsync(Ct);
        }

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.SkippedMessages, done.LlmCalls).ShouldBe(("completed", 20, 0, 3));
        await using var check = postgres.CreateDbContext();
        (await check.SuggestionAlternatives.CountAsync(a => a.RunId == run.Id, Ct)).ShouldBe(20 - stored.Length);
    }

    [Fact]
    public async Task Compare_runs_leave_the_analysis_summary_unchanged_but_for_the_alternatives_count()
    {
        var before = await (await h.GetAsync("/api/analysis/summary")).Content.ReadFromJsonAsync<AnalysisSummaryDto>(Ct);
        await StartCompareAsync(new CompareRunRequest(null, first.Id));
        await h.RunNextAsync();

        var after = await (await h.GetAsync("/api/analysis/summary")).Content.ReadFromJsonAsync<AnalysisSummaryDto>(Ct);
        after.ShouldBe(before! with { Alternatives = 20 });
    }

    [Fact]
    public async Task Invalid_requests_are_400_and_a_missing_chat_model_is_409()
    {
        (await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest(null, null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest([Guid.NewGuid()], first.Id))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest([], null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest([Guid.NewGuid()], null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest(null, Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest([.. Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid())], null)))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await h.SetChatModelAsync(null);
        (await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest(null, first.Id))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await using var check = postgres.CreateDbContext();
        (await check.AnalysisRuns.CountAsync(r => r.Kind == AnalysisRunKind.Compare, Ct)).ShouldBe(0);
    }

    /// <summary>A compare run over the first run that stops during its second model call; returns the stored messages.</summary>
    private async Task<(AnalysisRunDto Run, string[] Stored)> CrashAfterFirstGroupAsync()
    {
        using var stop = new CancellationTokenSource();
        var calls = h.Chat.Calls;
        h.Chat.Respond = (ids, call, _, ct) =>
        {
            if (call == calls + 2)
            {
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult(AnalysisRunHarness.Agree(ids));
        };
        var run = await StartCompareAsync(new CompareRunRequest(null, first.Id));
        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));

        await using var db = postgres.CreateDbContext();
        var stored = await db.SuggestionAlternatives.Where(a => a.RunId == run.Id).Select(a => a.MessageId).ToArrayAsync(Ct);
        stored.ShouldNotBeEmpty();
        return (run, stored);
    }

    private async Task<AnalysisRunDto> StartCompareAsync(CompareRunRequest request)
    {
        var response = await h.PostAsync("/api/analysis/compare-runs", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<AnalysisRunDto>(Ct)).ShouldNotBeNull();
    }

    private async Task<Guid[]> SuggestionIdsAsync(params string[] messageIds)
    {
        await using var db = postgres.CreateDbContext();
        return [.. await db.Suggestions.AsNoTracking().Where(s => messageIds.Contains(s.MessageId)).OrderBy(s => s.MessageId).Select(s => s.Id).ToListAsync(Ct)];
    }

    /// <summary>
    /// Every suggestion but its compare-run marker (which the run writes), message status and sender count, serialised
    /// in a fixed order.
    /// </summary>
    private async Task<string> SnapshotAsync()
    {
        await using var db = postgres.CreateDbContext();
        var suggestions = (await db.Suggestions.AsNoTracking().OrderBy(s => s.Id).ToListAsync(Ct))
            .Select(s => { s.CompareRunCreatedAt = null; return s; })
            .ToList();
        var messages = await db.Messages.AsNoTracking().OrderBy(m => m.Id).Select(m => new { m.Id, m.AnalysisStatus, m.UpdatedAt }).ToListAsync(Ct);
        var senders = await db.Senders.AsNoTracking().OrderBy(s => s.Address).Select(s => new { s.Address, s.AnalysedCount }).ToListAsync(Ct);
        return JsonSerializer.Serialize(new { suggestions, messages, senders });
    }

    private static DecisionRow Decision(string messageId, string sender, string? scope, string label, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = messageId,
        SenderAddress = sender,
        ScopeKey = scope,
        SubjectTemplate = SubjectNormaliser.Template($"Synthetic {messageId}"),
        TopicLabel = label,
        Outcome = DecisionOutcome.Approved,
        Source = SuggestionSource.Llm,
        CreatedAt = at,
    };
}
