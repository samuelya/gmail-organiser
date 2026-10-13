using System.Net;
using System.Net.Http.Json;
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
            decisions = await db.Decisions.CountAsync(Ct);
        }

        var source = (await IdsAsync("a00"))[0];
        var targets = await IdsAsync("a00", "a01", "a02", "a03", "b00");
        var response = await CopyAsync(source, [.. targets, targets[1], Guid.NewGuid()]);
        response.ShouldBe(new AlternativeCopyResponse(2, 4), "a01 and a03; the source, approved a02, b00 of another sender and an unknown id skipped");

        await using var check = postgres.CreateDbContext();
        var src = await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == source, Ct);
        var copies = await check.SuggestionAlternatives.AsNoTracking().Where(a => a.CopiedFromSuggestionId == source)
            .OrderBy(a => a.MessageId).ToListAsync(Ct);
        copies.Select(a => a.MessageId).ShouldBe(["a01", "a03"]);
        copies.ShouldAllBe(a => a.TopicLabel == Copied && a.DocumentTypeLabel == "Receipt" && a.NeedsAction
            && a.Reason == "Synthetic source reason" && a.RunId == src.RunId && a.Model == src.Model && a.MailType == src.MailType);
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
        (copy.CopiedFromSuggestionId, copy.TopicLabel).ShouldBe((null, Copied));
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
