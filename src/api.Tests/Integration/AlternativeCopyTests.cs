using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Copy an accepted outcome to similar pending suggestions as alternatives (#562).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AlternativeCopyTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Copied = "Copied topic";
    private const string Kept = "Label_kept";
    private const string Gone = "Label_gone";
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A normal run writes 20 suggestions; a00 (the source) then holds an edited outcome replacing two labels, and a01
    /// carries only one of them and already has a compare-run alternative.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        await using var db = postgres.CreateDbContext();
        await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Pending, s =>
        {
            s.TopicLabel = Copied;
            s.DocumentTypeLabel = "Receipt";
            s.NeedsAction = true;
            s.Reason = "Synthetic source reason";
            s.SetReplaced([(Kept, "Kept"), (Gone, "Gone")]);
        });
        await db.Messages.Where(m => m.Id == "a01")
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, new[] { "INBOX", Kept }), Ct);
        var a01 = await db.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a01", Ct);
        var compare = SuggestionAlternativeRow.From(a01, a01.Id, a01.RunId!.Value, DateTimeOffset.UtcNow);
        compare.TopicLabel = "Compared";
        db.SuggestionAlternatives.Add(compare);
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Copies_to_pending_suggestions_of_the_same_sender_and_skips_the_rest_without_touching_Gmail_or_decisions()
    {
        int decisions;
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a02", SuggestionStatus.Approved);
            await db.Messages.Where(m => m.Id == "a04").ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedInGmail, true), Ct);
            decisions = await db.Decisions.CountAsync(Ct);
        }

        var source = (await IdsAsync("a00"))[0];
        var targets = await IdsAsync("a00", "a01", "a02", "a03", "a04", "b00");
        var response = await CopyAsync(source, [.. targets, targets[1], Guid.NewGuid()]);
        response.ShouldBe(new AlternativeCopyResponse(2, 5),
            "a01 and a03; the source, approved a02, a04 deleted in Gmail, b00 of another sender and an unknown id skipped");

        await using var check = postgres.CreateDbContext();
        var src = await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == source, Ct);
        var copies = await check.SuggestionAlternatives.AsNoTracking().Where(a => a.CopiedFromSuggestionId == source)
            .OrderBy(a => a.MessageId).ToListAsync(Ct);
        copies.Select(a => a.MessageId).ShouldBe(["a01", "a03"]);
        copies.ShouldAllBe(a => a.TopicLabel == Copied && a.DocumentTypeLabel == "Receipt" && a.NeedsAction
            && a.Reason == "Synthetic source reason" && a.RunId == null && a.Copied && !a.Edited && a.Model == src.Model && a.MailType == src.MailType);
        copies[0].ReplaceLabelIds.ShouldBe([Kept]);
        copies[0].ReplaceLabels.ShouldBe(["Kept"]);
        copies[1].ReplaceLabelIds.ShouldBeEmpty();
        (await check.SuggestionAlternatives.CountAsync(a => a.MessageId == "a01", Ct)).ShouldBe(1, "the compare-run alternative is replaced");
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.MessageId == "a01", Ct)).TopicLabel.ShouldBe("Shopping");
        (await check.Decisions.CountAsync(Ct)).ShouldBe(decisions);
        (await check.ActionLog.CountAsync(Ct)).ShouldBe(0);

        var group = (await (await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.Shop}")).Content
            .ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull().Groups.Single(g => g.Members.Any(m => m.MessageId == "a01"));
        group.Members.Single(m => m.MessageId == "a01").Alternative.ShouldNotBeNull().Copied.ShouldBeTrue();
        group.Alternative.ShouldNotBeNull().Copied.ShouldBeTrue();
    }

    [Fact]
    public async Task An_accepted_copy_becomes_the_pending_outcome_and_a_deleted_source_leaves_the_copy_valid()
    {
        var source = (await IdsAsync("a00"))[0];
        var targets = await IdsAsync("a01", "a03");
        (await CopyAsync(source, targets)).ShouldBe(new AlternativeCopyResponse(2, 0));

        var accept = await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([targets[0]], null));
        (await accept.Content.ReadFromJsonAsync<AlternativeDecisionResponse>(Ct)).ShouldBe(new AlternativeDecisionResponse(1, 0, 0));

        await using var db = postgres.CreateDbContext();
        var a01 = await db.Suggestions.AsNoTracking().SingleAsync(s => s.Id == targets[0], Ct);
        (a01.Status, a01.TopicLabel, a01.NeedsAction).ShouldBe((SuggestionStatus.Pending, Copied, true));
        a01.ReplaceLabelIds.ShouldBe([Kept]);

        await db.Suggestions.Where(s => s.Id == source).ExecuteDeleteAsync(Ct);
        var copy = await db.SuggestionAlternatives.AsNoTracking().SingleAsync(a => a.SuggestionId == targets[1], Ct);
        (copy.CopiedFromSuggestionId, copy.Copied, copy.TopicLabel).ShouldBe((null, true, Copied));
        var shown = (await (await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.Shop}")).Content
                .ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull()
            .Groups.SelectMany(g => g.Members).Single(m => m.MessageId == "a03").Alternative.ShouldNotBeNull();
        (shown.Copied, shown.Comment).ShouldBe((true, null), "still a copy, without a run's comment");
    }

    [Fact]
    public async Task A_copy_carries_no_extras_so_accept_keeps_the_targets_own()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a00")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraLabels, new[] { "Synthetic Source Extra" }), Ct);
            await db.Suggestions.Where(s => s.MessageId == "a03")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraLabels, new[] { "Synthetic Own" }), Ct);
        }

        var source = (await IdsAsync("a00"))[0];
        var target = (await IdsAsync("a03"))[0];
        (await CopyAsync(source, [target])).ShouldBe(new AlternativeCopyResponse(1, 0));
        await using (var db = postgres.CreateDbContext())
        {
            (await db.SuggestionAlternatives.AsNoTracking().SingleAsync(a => a.SuggestionId == target, Ct)).ExtraLabels.ShouldBeEmpty();
        }

        var accept = await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([target], null));
        (await accept.Content.ReadFromJsonAsync<AlternativeDecisionResponse>(Ct)).ShouldBe(new AlternativeDecisionResponse(1, 0, 0));

        await using var check = postgres.CreateDbContext();
        var accepted = await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == target, Ct);
        (accepted.TopicLabel, accepted.ExtraLabels.Single()).ShouldBe((Copied, "Synthetic Own"));
    }

    [Fact]
    public async Task A_hand_edited_stage0_source_stays_edited_on_the_target_and_its_approval_is_recorded()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Pending, s =>
            {
                s.Source = SuggestionSource.Stage0;
                s.Edited = true;
            });
        }

        var source = (await IdsAsync("a00"))[0];
        var target = (await IdsAsync("a03"))[0];
        (await CopyAsync(source, [target])).ShouldBe(new AlternativeCopyResponse(1, 0));
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([target], null))).IsSuccessStatusCode.ShouldBeTrue();

        await using var check = postgres.CreateDbContext();
        var accepted = await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == target, Ct);
        (accepted.Source, accepted.Edited, accepted.TopicLabel).ShouldBe((SuggestionSource.Stage0, true, Copied));
        (await h.PostWithoutBodyAsync($"/api/review/suggestions/{target}/approve")).IsSuccessStatusCode.ShouldBeTrue();
        var decision = await check.Decisions.AsNoTracking().SingleAsync(d => d.MessageId == "a03", Ct);
        (decision.Source, decision.Edited, decision.TopicLabel).ShouldBe((SuggestionSource.Stage0, true, Copied));
    }

    [Fact]
    public async Task An_accepted_copy_keeps_the_source_run_so_its_approval_is_decided_under_the_document_type_parent_like_the_source()
    {
        await using var db = postgres.CreateDbContext();
        var runId = await db.Suggestions.Where(s => s.MessageId == "a00").Select(s => s.RunId).SingleAsync(Ct);
        await db.AnalysisRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DocumentTypeParent, "Documents"), Ct);
        var source = (await IdsAsync("a00"))[0];
        var target = (await IdsAsync("a03"))[0];
        (await CopyAsync(source, [target])).ShouldBe(new AlternativeCopyResponse(1, 0));
        var copy = await db.SuggestionAlternatives.AsNoTracking().SingleAsync(a => a.SuggestionId == target, Ct);
        (copy.RunId, copy.OutcomeRunId).ShouldBe((null, runId), "not a run answer, but carries the source's run");

        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([target], null))).IsSuccessStatusCode.ShouldBeTrue();
        (await db.Suggestions.AsNoTracking().SingleAsync(s => s.Id == target, Ct)).RunId.ShouldBe(runId);
        (await h.PostWithoutBodyAsync($"/api/review/suggestions/{source}/approve")).IsSuccessStatusCode.ShouldBeTrue();
        (await h.PostWithoutBodyAsync($"/api/review/suggestions/{target}/approve")).IsSuccessStatusCode.ShouldBeTrue();
        var decisions = await db.Decisions.AsNoTracking().Where(d => d.MessageId == "a00" || d.MessageId == "a03")
            .OrderBy(d => d.MessageId).ToListAsync(Ct);
        decisions.Select(d => (d.DocumentTypeDecided, d.DocumentTypeParent, d.DocumentTypeLabel))
            .ShouldBe([(true, "Documents", "Receipt"), (true, "Documents", "Receipt")]);
    }

    [Fact]
    public async Task Copies_of_an_outcome_accepted_from_a_compare_run_are_not_that_runs_answers()
    {
        var ids = await IdsAsync("a00", "a01", "a03");
        var compare = await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest(ids, null));
        var run = (await compare.Content.ReadFromJsonAsync<AnalysisRunDto>(Ct)).ShouldNotBeNull();
        await h.RunNextAsync();
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([ids[0]], null))).IsSuccessStatusCode.ShouldBeTrue();
        (await CopyAsync(ids[0], [ids[1], ids[2]])).ShouldBe(new AlternativeCopyResponse(2, 0), "replacing the run's own answers");

        // Compare run R again: only the suggestion R wrote, not the ones that hold copies of it.
        var again = await h.PostAsync("/api/analysis/compare-runs", new CompareRunRequest(null, run.Id));
        var rerun = (await again.Content.ReadFromJsonAsync<AnalysisRunDto>(Ct)).ShouldNotBeNull();
        rerun.RequestedCount.ShouldBe(1);

        // R failed and its job is gone: the rebuilt cursor does not count the copies as R's answers.
        await using var db = postgres.CreateDbContext();
        await db.AnalysisRuns.Where(r => r.Id == rerun.Id).ExecuteDeleteAsync(Ct);
        await db.Jobs.Where(j => j.Id == rerun.JobId).ExecuteDeleteAsync(Ct);
        await db.AnalysisRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AnalysisRunStatus.Failed), Ct);
        await db.Jobs.Where(j => j.Id == run.JobId).ExecuteDeleteAsync(Ct);
        (await h.PostWithoutBodyAsync($"/api/analysis/runs/{run.Id}/resume")).IsSuccessStatusCode.ShouldBeTrue();
        var jobId = await db.AnalysisRuns.Where(r => r.Id == run.Id).Select(r => r.JobId).SingleAsync(Ct);
        var cursor = await db.Jobs.Where(j => j.Id == jobId).Select(j => j.Cursor).SingleAsync(Ct);
        var covered = JsonSerializer.Deserialize<AnalysisRunCursor>(cursor!, JsonSerializerOptions.Web)!.CoveredIds ?? [];
        covered.ShouldNotContain("a01");
        covered.ShouldNotContain("a03");
    }

    [Fact]
    public async Task Invalid_requests_are_400_unknown_source_404_and_an_active_analysis_run_409()
    {
        var source = (await IdsAsync("a00"))[0];
        var targets = await IdsAsync("a01");
        (await h.PostAsync("/api/review/alternatives/copy", new AlternativeCopyRequest(null, targets))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/alternatives/copy", new AlternativeCopyRequest(source, []))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/alternatives/copy", new AlternativeCopyRequest(source, null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var tooMany = Enumerable.Range(0, SettingsValidation.MaxAnalysisDefaultCount + 1).Select(_ => Guid.NewGuid()).ToArray();
        (await h.PostAsync("/api/review/alternatives/copy", new AlternativeCopyRequest(source, tooMany))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await h.PostAsync("/api/review/alternatives/copy", new AlternativeCopyRequest(Guid.NewGuid(), targets))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var queued = Guid.CreateVersion7();
        await using var db = postgres.CreateDbContext();
        db.AnalysisRuns.Add(new AnalysisRunRow
        {
            Id = queued,
            Kind = AnalysisRunKind.Compare,
            Scope = AnalysisScope.Messages,
            Status = AnalysisRunStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        (await h.PostAsync("/api/review/alternatives/copy", new AlternativeCopyRequest(source, targets))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await db.SuggestionAlternatives.CountAsync(a => a.CopiedFromSuggestionId != null, Ct)).ShouldBe(0);
        await db.AnalysisRuns.Where(r => r.Id == queued).ExecuteDeleteAsync(Ct);
    }

    private async Task<AlternativeCopyResponse> CopyAsync(Guid source, Guid[] targets)
    {
        var response = await h.PostAsync("/api/review/alternatives/copy", new AlternativeCopyRequest(source, targets));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AlternativeCopyResponse>(Ct)).ShouldNotBeNull();
    }

    private async Task<Guid[]> IdsAsync(params string[] messageIds)
    {
        await using var db = postgres.CreateDbContext();
        return [.. await db.Suggestions.AsNoTracking().Where(s => messageIds.Contains(s.MessageId)).OrderBy(s => s.MessageId).Select(s => s.Id).ToListAsync(Ct)];
    }
}
