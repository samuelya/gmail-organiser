using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.CleanUp;
using GmailOrganiser.Common;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Review alternatives (#249): an applied suggestion that took an alternative and was applied again.</summary>
public sealed partial class UndoActionsJobTests
{
    [Fact]
    public async Task Undo_of_an_earlier_batch_leaves_a_suggestion_accepted_and_applied_since_and_counts_it_once()
    {
        await SeedAsync(("a00", "Synthetic/First", false, false));
        var first = await ApplyAndRunAsync();
        (await AppliedCountAsync()).ShouldBe(1);

        var id = await AddAlternativeAsync("a00", "Synthetic/Second");
        var accept = await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([id], null));
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await accept.Content.ReadFromJsonAsync<AlternativeDecisionResponse>(Ct)).ShouldBe(new AlternativeDecisionResponse(1, 0, 0));
        (await AppliedCountAsync()).ShouldBe(0);

        await using (var db = postgres.CreateDbContext())
        {
            var suggestion = await db.Suggestions.SingleAsync(s => s.Id == id, Ct);
            suggestion.SetStatus(SuggestionStatus.Approved, await db.Messages.SingleAsync(m => m.Id == "a00", Ct), DateTimeOffset.UtcNow.AddSeconds(-1));
            await db.SaveChangesAsync(Ct);
        }

        var second = await ApplyAndRunAsync();
        (await AppliedCountAsync()).ShouldBe(1);

        await UndoAsync(first.Id);
        await h.RunNextAsync();
        (await StatusAsync(id)).ShouldBe(SuggestionStatus.Applied);
        (await AppliedCountAsync()).ShouldBe(1);

        await UndoAsync(second.Id);
        await h.RunNextAsync();
        (await StatusAsync(id)).ShouldBe(SuggestionStatus.Approved);
        (await AppliedCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Applying_an_accepted_alternative_removes_the_applied_outcomes_labels_and_undo_restores_them()
    {
        await SeedAsync(("a00", "Synthetic/First", false, true));
        await ApplyAndRunAsync();
        var applied = Labels("a00").ToArray();
        var names = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        applied.ShouldContain(names[DeleteLabel]);
        applied.ShouldContain(names["Synthetic/First"]);

        var id = await AddAlternativeAsync("a00", "Synthetic/Second");
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([id], null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        string sender;
        await using (var db = postgres.CreateDbContext())
        {
            sender = (await db.Messages.SingleAsync(m => m.Id == "a00", Ct)).FromAddress;
        }

        // The review card shows the removal before the user approves.
        var detail = await GetAsync<ReviewSenderDetailDto>($"/api/review/senders/{sender}");
        var card = detail.Groups.SelectMany(g => g.Members).Single(m => m.Id == id);
        card.ReplaceLabels.ShouldBe(new[] { DeleteLabel, "Synthetic/First" }.Order(StringComparer.Ordinal));
        card.LabelChange.ShouldBe(LabelChange.Relabel);

        (await h.PostAsync($"/api/review/suggestions/{id}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await ApplyAndRunAsync();
        names = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        Labels("a00").ShouldContain(names["Synthetic/Second"]);
        Labels("a00").ShouldNotContain(names["Synthetic/First"]);
        Labels("a00").ShouldNotContain(names[DeleteLabel]);
        (await GetAsync<PagedDto<CleanupMessageDto>>($"/api/clean-up/senders/{sender}/messages")).Items.ShouldNotContain(m => m.Id == "a00");

        await UndoAsync(second.Id);
        await h.RunNextAsync();
        Labels("a00").ShouldBe(applied, ignoreOrder: true);
    }

    [Fact]
    public async Task An_edit_after_accepting_keeps_the_applied_delete_label_replaced_but_never_adds_one()
    {
        await SeedAsync(("a00", "Synthetic/First", false, true), ("a01", "Synthetic/First", false, true), ("a02", "Synthetic/First", false, false));
        await ApplyAndRunAsync();
        var names = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        string[] carried = [.. Labels("a02"), names[DeleteLabel]];
        h.Gmail.Inner.SetLabels("a02", carried);
        string sender;
        await using (var db = postgres.CreateDbContext())
        {
            await db.Messages.Where(m => m.Id == "a02").ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, carried), Ct);
            sender = (await db.Messages.SingleAsync(m => m.Id == "a00", Ct)).FromAddress;
        }

        Guid[] ids = [await AddAlternativeAsync("a00", "Synthetic/Second"), await AddAlternativeAsync("a01", "Synthetic/Second"),
            await AddAlternativeAsync("a02", "Synthetic/Second")];
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest(ids, null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var card = (await GetAsync<ReviewSenderDetailDto>($"/api/review/senders/{sender}")).Groups.SelectMany(g => g.Members).Single(m => m.Id == ids[0]);

        // The card's own list, delete label included, and a list without it: both keep removing it.
        (await h.PutAsync($"/api/review/suggestions/{ids[0]}", new EditSuggestionRequest("Synthetic/Second", false, false, [.. card.ReplaceLabels])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await h.PutAsync($"/api/review/suggestions/{ids[1]}", new EditSuggestionRequest("Synthetic/Second", false, false, ["Synthetic/First"])))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        // a02 carries the delete label but its applied outcome never set it: naming it is adding one.
        var added = await h.PutAsync($"/api/review/suggestions/{ids[2]}", new EditSuggestionRequest("Synthetic/Second", false, false, ["Synthetic/First", DeleteLabel]));
        added.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await added.Content.ReadAsStringAsync(Ct)).ShouldContain(DeleteLabel);

        await ApplyAndRunAsync();
        names = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        foreach (var id in new[] { "a00", "a01" })
        {
            Labels(id).ShouldContain(names["Synthetic/Second"]);
            Labels(id).ShouldNotContain(names["Synthetic/First"]);
            Labels(id).ShouldNotContain(names[DeleteLabel]);
        }

        (await GetAsync<PagedDto<CleanupMessageDto>>($"/api/clean-up/senders/{sender}/messages")).Items
            .ShouldNotContain(m => m.Id == "a00" || m.Id == "a01");
    }

    private async Task<Guid> AddAlternativeAsync(string messageId, string topic)
    {
        await using var db = postgres.CreateDbContext();
        var id = await db.Suggestions.Where(s => s.MessageId == messageId).Select(s => s.Id).SingleAsync(Ct);
        db.SuggestionAlternatives.Add(new SuggestionAlternativeRow
        {
            Id = Guid.CreateVersion7(),
            SuggestionId = id,
            MessageId = messageId,
            Source = SuggestionSource.Llm,
            TopicLabel = topic,
            Confidence = 0.8,
            Reason = "Synthetic reason",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<int> AppliedCountAsync()
    {
        await using var db = postgres.CreateDbContext();
        return await db.Senders.SumAsync(s => s.AppliedCount, Ct);
    }

    private async Task<SuggestionStatus> StatusAsync(Guid id)
    {
        await using var db = postgres.CreateDbContext();
        return (await db.Suggestions.AsNoTracking().SingleAsync(s => s.Id == id, Ct)).Status;
    }
}
