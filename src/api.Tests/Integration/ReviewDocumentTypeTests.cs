using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Document-type labels in review (#242): DTOs, edit validation, group approve and apply to rest of sender.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewDocumentTypeTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Parent = "Synthetic Types";
    private const string Existing = Parent + "/Receipt";
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
        await h.Gmail.Inner.CreateLabelAsync(Parent, Ct);
        await h.Gmail.Inner.CreateLabelAsync(Existing, Ct);
        h.Services.GetRequiredService<LabelCatalog>().Invalidate();
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
        await SetParentAsync(Parent);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Theory]
    [InlineData("Other/Receipt", "Must be 1 to 3 levels under 'Synthetic Types'.")]
    [InlineData(Parent + "/Receipt/Paper/Scan/Copy", "Must be 1 to 3 levels under 'Synthetic Types'.")]
    [InlineData(Parent, "Must be 1 to 3 levels under 'Synthetic Types'.")]
    [InlineData(Parent + "/ Receipt", "Not a label path Gmail accepts.")]
    [InlineData(Parent + "/Shopping", "Must differ from the topic label.")]
    public async Task Edit_refuses_a_document_type_label_outside_the_rule(string type, string error)
    {
        var response = await h.PutAsync($"/api/review/suggestions/{await IdAsync("a01")}", Edit(Parent + "/shopping", type));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull()
            .Errors[DocumentTypeEdit.Field].ShouldBe([error]);
    }

    [Fact]
    public async Task Edit_refuses_a_document_type_label_while_the_parent_is_off()
    {
        await SetParentAsync(null);

        var response = await h.PutAsync($"/api/review/suggestions/{await IdAsync("a01")}", Edit("Shopping", Existing));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull()
            .Errors[DocumentTypeEdit.Field].ShouldBe([DocumentTypeEdit.OffMessage]);
        (await EditAsync("a01", Edit("Shopping", ""))).DocumentTypeLabel.ShouldBeNull();
    }

    [Fact]
    public async Task Edit_sets_changes_keeps_and_clears_the_document_type_label()
    {
        var set = await EditAsync("a01", Edit("Shopping", " synthetic types/Invoice "));
        (set.DocumentTypeLabel, set.DocumentTypeIsNew, set.Edited).ShouldBe((Parent + "/Invoice", true, true));

        // Stored in Gmail's spelling, as analysis stores it, so group and sender-pattern matching agree.
        var changed = await EditAsync("a01", Edit("Shopping", Existing.ToUpperInvariant()));
        (changed.DocumentTypeLabel, changed.DocumentTypeIsNew).ShouldBe((Existing, false));

        (await EditAsync("a01", Edit("Shopping", null))).DocumentTypeLabel.ShouldBe(Existing);
        var cleared = await EditAsync("a01", Edit("Shopping", "  "));
        (cleared.DocumentTypeLabel, cleared.DocumentTypeIsNew).ShouldBe((null, false));

        var nested = await EditAsync("a01", Edit("Shopping", " synthetic types/Bills/Power/Peak "));
        (nested.DocumentTypeLabel, nested.DocumentTypeIsNew).ShouldBe((Parent + "/Bills/Power/Peak", true));
    }

    [Fact]
    public async Task Edit_refuses_a_topic_label_equal_to_the_document_type_it_keeps()
    {
        await EditAsync("a01", Edit("Shopping", Existing));

        var response = await h.PutAsync($"/api/review/suggestions/{await IdAsync("a01")}", Edit(Existing.ToLowerInvariant(), null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull()
            .Errors[DocumentTypeEdit.Field].ShouldBe([DocumentTypeEdit.SameAsTopicMessage]);
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.SingleAsync(s => s.MessageId == "a01", Ct)).TopicLabel.ShouldBe("Shopping");
    }

    [Fact]
    public async Task A_claude_alternative_drops_a_document_type_equal_to_its_topic_label()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId == "a01").ExecuteUpdateAsync(s => s.SetProperty(x => x.DocumentTypeLabel, Existing), Ct);
        }

        await using (var scope = h.Services.CreateAsyncScope())
        {
            var edit = new GroupEdit(Existing, false, false, [], DocumentTypeChange.Unchanged);
            (await scope.ServiceProvider.GetRequiredService<ReviewService>().ApprovePendingAsync(await IdAsync("a01"), edit, Ct))
                .Changed.ShouldBe(1);
        }

        await using var check = postgres.CreateDbContext();
        var row = await check.Suggestions.SingleAsync(s => s.MessageId == "a01", Ct);
        (row.TopicLabel, row.DocumentTypeLabel, row.Status).ShouldBe((Existing, null, SuggestionStatus.Approved));
    }

    [Fact]
    public async Task Review_dtos_carry_the_document_type_and_group_approve_takes_only_the_shown_type()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ExecuteUpdateAsync(
                s => s.SetProperty(x => x.DocumentTypeLabel, Parent + "/Invoice").SetProperty(x => x.DocumentTypeIsNew, true), Ct);
            await db.Suggestions.Where(s => s.MessageId == "a01").ExecuteUpdateAsync(
                s => s.SetProperty(x => x.DocumentTypeLabel, Existing).SetProperty(x => x.DocumentTypeIsNew, false), Ct);
        }

        var group = (await DetailAsync(AnalysisRunHarness.Shop)).Groups.Single();
        (group.DocumentTypeLabel, group.DocumentTypeIsNew, group.Mixed).ShouldBe((Parent + "/Invoice", true, true));
        var member = group.Members.Single(m => m.MessageId == "a01");
        (member.DocumentTypeLabel, member.DocumentTypeIsNew).ShouldBe((Existing, false));

        var response = await h.PostAsync("/api/review/groups/approve", new GroupDecisionRequest(
            AnalysisRunHarness.Shop, group.GroupKey, group.TopicLabel, group.NeedsAction, group.ToBeDeleted, null, group.DocumentTypeLabel));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var result = (await response.Content.ReadFromJsonAsync<GroupDecisionResponse>(Ct)).ShouldNotBeNull();
        (result.Changed, result.Skipped.ShouldHaveSingleItem()).ShouldBe((group.Size - 1, await IdAsync("a01")));
    }

    [Fact]
    public async Task Apply_rest_takes_the_patterns_document_type_and_writes_it()
    {
        // Billing: c00 and c01 approved with the existing type, c03 with the same topic and none; c02 remains.
        await SeedBillingAsync(null, "c02");

        var pattern = await h.GetAsync($"/api/review/senders/{AnalysisRunHarness.Billing}/pattern");
        var dto = (await pattern.Content.ReadFromJsonAsync<SenderPatternDto>(Ct)).ShouldNotBeNull();
        // The type is the most common within the outcome; it does not split the agreement (all three share the topic).
        (dto.DocumentTypeLabel, dto.Approvals, dto.Agreement, dto.Remaining).ShouldBe((Existing, 3, 1.0, 1));

        var off = await h.PostAsync($"/api/review/senders/{AnalysisRunHarness.Billing}/apply-rest", new ApplyRestRequest(DocumentTypeLabel: "Other/Type"));
        off.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var response = await h.PostAsync($"/api/review/senders/{AnalysisRunHarness.Billing}/apply-rest", new ApplyRestRequest());
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        await h.RunNextAsync();

        await using var check = postgres.CreateDbContext();
        var created = await check.Suggestions.AsNoTracking()
            .Where(s => s.Source == SuggestionSource.SenderPattern).ToListAsync(Ct);
        var row = created.ShouldHaveSingleItem();
        (row.MessageId, row.DocumentTypeLabel, row.DocumentTypeIsNew, row.Status).ShouldBe(("c02", Existing, false, SuggestionStatus.Applied));
        var existingId = (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == Existing).Id;
        h.Gmail.Inner.Messages.Single(m => m.Id == "c02").LabelIds.ShouldContain(existingId);
    }

    [Fact]
    public async Task Apply_rest_refuses_a_requested_type_equal_to_the_patterns_topic_and_drops_the_patterns_own()
    {
        await SeedBillingAsync(Parent + "/Bills", "c02", "c03");
        var url = $"/api/review/senders/{AnalysisRunHarness.Billing}/apply-rest";

        var clash = await h.PostAsync(url, new ApplyRestRequest(DocumentTypeLabel: Parent + "/bills"));
        clash.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await clash.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull()
            .Errors[DocumentTypeEdit.Field].ShouldBe([DocumentTypeEdit.SameAsTopicMessage]);

        // The pattern's type (the existing one) is the requested topic: derived, so it is dropped rather than refused.
        (await h.PostAsync(url, new ApplyRestRequest(TopicLabel: Existing))).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await PatternRowsAsync()).Select(r => (r.TopicLabel, r.DocumentTypeLabel)).ShouldBe([(Existing, null), (Existing, null)]);
    }

    [Fact]
    public async Task Apply_rest_marks_a_requested_document_type_gmail_lacks_as_new()
    {
        await SeedBillingAsync(Parent + "/Bills", "c02", "c03");

        var response = await h.PostAsync(
            $"/api/review/senders/{AnalysisRunHarness.Billing}/apply-rest", new ApplyRestRequest(DocumentTypeLabel: Parent + "/Invoice"));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        (await PatternRowsAsync()).Select(r => (r.DocumentTypeLabel, r.DocumentTypeIsNew))
            .ShouldBe([(Parent + "/Invoice", true), (Parent + "/Invoice", true)]);
    }

    /// <summary>
    /// Approves every Billing suggestion (c00–c03) with <paramref name="topic"/> (the analysed one when null) and the
    /// existing type, c03 without one; the <paramref name="remaining"/> messages then lose their suggestion.
    /// </summary>
    private async Task SeedBillingAsync(string? topic, params string[] remaining)
    {
        await using var db = postgres.CreateDbContext();
        var billing = db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Billing);
        await billing.ExecuteUpdateAsync(s => s.SetProperty(x => x.DocumentTypeLabel, Existing), Ct);
        await db.Suggestions.Where(s => s.MessageId == "c03").ExecuteUpdateAsync(s => s.SetProperty(x => x.DocumentTypeLabel, (string?)null), Ct);
        if (topic is not null)
        {
            await billing.ExecuteUpdateAsync(s => s.SetProperty(x => x.TopicLabel, topic), Ct);
        }

        foreach (var id in await billing.Select(s => s.Id).ToListAsync(Ct))
        {
            (await h.PostAsync($"/api/review/suggestions/{id}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await db.Suggestions.Where(s => remaining.Contains(s.MessageId)).ExecuteDeleteAsync(Ct);
        await db.Messages.Where(m => remaining.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.AnalysisStatus, AnalysisStatus.NotAnalysed), Ct);
    }

    private async Task<List<SuggestionRow>> PatternRowsAsync()
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.AsNoTracking()
            .Where(s => s.Source == SuggestionSource.SenderPattern).OrderBy(s => s.MessageId).ToListAsync(Ct);
    }

    private static EditSuggestionRequest Edit(string topic, string? type) => new(topic, false, false, null, type);

    private async Task<SuggestionDto> EditAsync(string messageId, EditSuggestionRequest request)
    {
        var response = await h.PutAsync($"/api/review/suggestions/{await IdAsync(messageId)}", request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<SuggestionDto>(Ct)).ShouldNotBeNull();
    }

    private async Task SetParentAsync(string? parent)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { DocumentTypeParent = parent }, Ct);
    }

    private async Task<Guid> IdAsync(string messageId)
    {
        await using var db = postgres.CreateDbContext();
        return await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
    }

    private async Task<ReviewSenderDetailDto> DetailAsync(string address)
    {
        var response = await h.GetAsync($"/api/review/senders/{Uri.EscapeDataString(address)}?status=pending");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ReviewSenderDetailDto>(Ct)).ShouldNotBeNull();
    }
}
