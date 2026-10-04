using GmailOrganiser.Analysis;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Labelled phase (#200): apply removes the replaced labels in the same call; undo puts them back.</summary>
public sealed partial class UndoActionsJobTests
{
    [Fact]
    public async Task A_relabel_removes_the_replaced_label_and_undo_restores_it()
    {
        var old = await h.Gmail.Inner.CreateLabelAsync("Old/Bills", Ct);
        await SetLabelsAsync("a00", ["INBOX", "CATEGORY_UPDATES", old.Id]);
        await SetLabelsAsync("a01", ["INBOX", "CATEGORY_UPDATES"]);
        await SeedReplacingAsync("a00", "New/Bills", ["old/bills"]);
        await SeedReplacingAsync("a01", "New/Bills", ["Gone/Label"]);
        var before = new[] { "a00", "a01" }.ToDictionary(id => id, Labels);

        var apply = await ApplyAndRunAsync();

        var ids = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        ids.ShouldContainKey("Old/Bills");
        Labels("a00").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"]], ignoreOrder: true);
        Labels("a01").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"]], ignoreOrder: true);
        await using (var db = postgres.CreateDbContext())
        {
            var log = await db.ActionLog.SingleAsync(l => l.BatchId == apply.Id && l.MessageId == "a00", Ct);
            log.LabelsRemoved.ShouldBe(["Old/Bills", "INBOX"], ignoreOrder: true);
            log.LabelIdsBefore.ShouldContain(old.Id);
            log.LabelIdsAfter.ShouldNotContain(old.Id);
            (await db.Messages.SingleAsync(m => m.Id == "a00", Ct)).LabelIds.ShouldBe(Labels("a00"), ignoreOrder: true);
        }

        await UndoAsync(apply.Id);
        await h.RunNextAsync();

        Labels("a00").ShouldBe(before["a00"], ignoreOrder: true);
        Labels("a01").ShouldBe(before["a01"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_replaced_label_deleted_in_gmail_before_the_send_is_dropped_and_the_chunk_applied()
    {
        var old = await h.Gmail.Inner.CreateLabelAsync("Old/Bills", Ct);
        await SetLabelsAsync("a00", ["INBOX", "CATEGORY_UPDATES", old.Id]);
        await SeedReplacingAsync("a00", "New/Bills", ["Old/Bills"]);
        h.Gmail.BeforeBatchModify = (call, _) =>
        {
            if (call == 1)
            {
                h.Gmail.Inner.DeleteLabel(old.Id);
            }

            return Task.CompletedTask;
        };

        var apply = await ApplyAndRunAsync();

        apply.MessageCount.ShouldBe(1);
        var ids = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        Labels("a00").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"]], ignoreOrder: true);
    }

    private async Task SetLabelsAsync(string id, string[] labels)
    {
        h.Gmail.Inner.SetLabels(id, labels);
        await using var db = postgres.CreateDbContext();
        await db.Messages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.LabelIds, labels), Ct);
    }

    private async Task SeedReplacingAsync(string id, string topic, string[] replace)
    {
        await SeedAsync((id, topic, false, false));
        await using var db = postgres.CreateDbContext();
        await db.Suggestions.Where(s => s.MessageId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReplaceLabels, replace), Ct);
    }
}
