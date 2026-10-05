using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The approved label set (#367) over an inbox run of the harness mailbox: shop 10 and news 6 get new labels, billing's
/// 4 a label with a blocked level; the taxonomy is locked with a cap of one new label.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ApprovedLabelSetTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Blocked = "Synthetic Blocked";

    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await SettingsAsync(x => x with { TaxonomyLocked = true, AnalysisMaxNewLabelsPerRun = 1, AnalysisBlockedLabels = [Blocked] });
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Answer(ids, (id, _) =>
            id.StartsWith('c') ? $"Finance/{Blocked.ToUpperInvariant()}" : AnalysisRunHarness.LabelFor(id)));
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task A_locked_run_fails_blocked_labels_proposes_new_ones_and_caps_them()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.FailedMessages, done.LlmCalls, done.NewLabelsProposed).ShouldBe(("completed", 16, 4, 4, 2));
        var system = h.Chat.Requests[0][0].Text;
        system.ShouldContain($"{AnalysisPromptBuilder.BlockedLabelsHeading} `{Blocked}`.");
        system.ShouldContain(AnalysisPromptBuilder.TaxonomyLockedText);

        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().ToListAsync(Ct);
        rows.Count.ShouldBe(16);
        rows.ShouldAllBe(s => s.IsNewLabel && !s.MessageId.StartsWith('c'));
        rows.Where(s => s.Source == SuggestionSource.Llm).ShouldAllBe(s => s.ProposedNewLabel == s.TopicLabel);

        // One of the two new labels is admitted; every row of the other is capped, derived ones included.
        var capped = rows.Where(s => s.Reason.EndsWith(ApprovedLabelSet.CapNote)).ToList();
        capped.Select(s => s.TopicLabel).Distinct().ShouldHaveSingleItem();
        capped.ShouldAllBe(s => s.Confidence <= ApprovedLabelSet.CappedConfidence);
        capped.Count.ShouldBeOneOf(6, 10);
        rows.Except(capped).ShouldAllBe(s => s.Confidence > ApprovedLabelSet.CappedConfidence);
        (await db.Messages.Where(m => m.Id.StartsWith("c")).Select(m => m.AnalysisStatus).ToListAsync(Ct))
            .ShouldAllBe(s => s == AnalysisStatus.NotAnalysed);
    }

    [Fact]
    public async Task Bulk_approve_skips_new_labels_until_approved_individually_or_edited_to_an_existing_label()
    {
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        var bulk = new BulkApproveRequest(SettingsValidation.MinBulkApproveThreshold, IncludeDerived: true);

        (await PostAsync<BulkApproveResponse>("/api/review/bulk-approve", bulk)).Approved.ShouldBe(0);
        var group = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.ShouldHaveSingleItem();
        group.NewLabelPending.ShouldBeTrue();
        group.Members.ShouldAllBe(m => m.NewLabelPending);

        var (approveId, editId) = (group.Members[0].Id, group.Members[1].Id);
        (await PostAsync<SuggestionDto>($"/api/review/suggestions/{approveId}/approve", new { })).NewLabelPending.ShouldBeFalse();
        var edit = await h.PutAsync($"/api/review/suggestions/{editId}", new EditSuggestionRequest(FakeLabelStore.SeedUserLabelNames[0], false, false));
        edit.StatusCode.ShouldBe(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(Ct));
        var edited = (await edit.Content.ReadFromJsonAsync<SuggestionDto>(Ct)).ShouldNotBeNull();
        // An edit approves the changed outcome.
        (edited.IsNewLabel, edited.NewLabelPending, edited.Status).ShouldBe((false, false, "approved"));

        (await PostAsync<BulkApproveResponse>("/api/review/bulk-approve", bulk)).Approved.ShouldBe(0);

        // Unlocked, the remaining new-label suggestions are bulk-approved again.
        await SettingsAsync(x => x with { TaxonomyLocked = false });
        (await PostAsync<BulkApproveResponse>("/api/review/bulk-approve", bulk)).Approved.ShouldBe(14);
    }

    private async Task SettingsAsync(Func<AppSettings, AppSettings> change)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(change, Ct);
    }

    private async Task<T> PostAsync<T>(string path, object body)
        where T : class
    {
        var response = await h.PostAsync(path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(Ct)).ShouldNotBeNull();
    }

    private async Task<ReviewSenderDetailDto> DetailAsync(string address)
    {
        var response = await h.GetAsync($"/api/review/senders/{Uri.EscapeDataString(address)}?status=pending");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull();
    }
}
