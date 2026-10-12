using System.Net;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Additional labels (#523): apply adds them in the same call as the topic label; undo removes them.</summary>
public sealed partial class UndoActionsJobTests
{
    [Fact]
    public async Task Apply_adds_new_and_existing_additional_labels_in_per_set_chunks_logs_them_and_undo_removes_them()
    {
        await h.Gmail.Inner.CreateLabelAsync("Synthetic", Ct);
        var existing = await h.Gmail.Inner.CreateLabelAsync("Synthetic/Kept", Ct);
        await SeedAsync(("a00", "New/Bills", false, false), ("a01", "New/Bills", false, false), ("a02", "New/Bills", false, false));
        await SetExtrasAsync("a00", "Synthetic/Kept", "Synthetic/Fresh");
        await SetExtrasAsync("a01", "Synthetic/Kept");
        var before = new[] { "a00", "a01", "a02" }.ToDictionary(id => id, Labels);
        var calls = h.Gmail.BatchModifyCalls.Count;

        var apply = await ApplyAndRunAsync();

        apply.MessageCount.ShouldBe(3);
        var ids = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        Labels("a00").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"], existing.Id, ids["Synthetic/Fresh"]], ignoreOrder: true);
        Labels("a01").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"], existing.Id], ignoreOrder: true);
        Labels("a02").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"]], ignoreOrder: true);
        h.Gmail.BatchModifyCalls.Skip(calls).Select(c => string.Join(",", c)).ShouldBe(["a00", "a01", "a02"], ignoreOrder: true);
        (await DetailAsync(apply.Id)).CreatedLabels.Select(l => l.Name).ShouldBe(["New", "New/Bills", "Synthetic/Fresh"], ignoreOrder: true);
        await using (var db = postgres.CreateDbContext())
        {
            var logs = await db.ActionLog.Where(l => l.BatchId == apply.Id).ToDictionaryAsync(l => l.MessageId, Ct);
            logs["a00"].LabelsAdded.ShouldBe(["New/Bills", "Synthetic/Kept", "Synthetic/Fresh"], ignoreOrder: true);
            logs["a01"].LabelsAdded.ShouldBe(["New/Bills", "Synthetic/Kept"], ignoreOrder: true);
            logs["a02"].LabelsAdded.ShouldBe(["New/Bills"]);
        }

        await UndoAsync(apply.Id);
        await h.RunNextAsync();

        foreach (var (id, labels) in before)
        {
            Labels(id).ShouldBe(labels, ignoreOrder: true);
        }
    }

    [Fact]
    public async Task An_additional_label_that_is_now_an_app_label_or_invalid_is_skipped_and_the_message_gets_its_other_labels()
    {
        await SeedAsync(("a00", "Synthetic/Topic", false, false));
        await SetExtrasAsync("a00", DeleteLabel, "Synthetic/ not a path", "Synthetic/Extra");

        var apply = await ApplyAndRunAsync();

        apply.MessageCount.ShouldBe(1);
        var ids = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        ids.ShouldNotContainKey(DeleteLabel);
        Labels("a00").ShouldBe(["CATEGORY_UPDATES", ids["Synthetic/Topic"], ids["Synthetic/Extra"]], ignoreOrder: true);
    }

    [Fact]
    public async Task Accepting_an_alternative_on_applied_mail_keeps_the_suggestions_additional_labels()
    {
        await SeedAsync(("a00", "Synthetic/First", false, false));
        await SetExtrasAsync("a00", "Synthetic/Extra");
        await ApplyAndRunAsync();
        var extra = (await h.Gmail.Inner.ListLabelsAsync(Ct)).Single(l => l.Name == "Synthetic/Extra").Id;
        Labels("a00").ShouldContain(extra);

        var id = await AddAlternativeAsync("a00", "Synthetic/Second");
        (await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([id], null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var db = postgres.CreateDbContext())
        {
            var suggestion = await db.Suggestions.SingleAsync(s => s.Id == id, Ct);
            suggestion.ExtraLabels.ShouldBe(["Synthetic/Extra"]);
            suggestion.ReplaceLabels.ShouldBe(["Synthetic/First"]);
        }

        (await h.PostAsync($"/api/review/suggestions/{id}/approve", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApplyAndRunAsync();

        var names = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        Labels("a00").ShouldContain(names["Synthetic/Second"]);
        Labels("a00").ShouldContain(extra);
        Labels("a00").ShouldNotContain(names["Synthetic/First"]);
    }

    private async Task SetExtrasAsync(string id, params string[] extras)
    {
        await using var db = postgres.CreateDbContext();
        await db.Suggestions.Where(s => s.MessageId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExtraLabels, extras), Ct);
    }
}
