using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Document-type labels (#242): apply adds them in the same call as the topic label; undo removes them.</summary>
public sealed partial class UndoActionsJobTests
{
    [Fact]
    public async Task Apply_adds_new_and_existing_document_type_labels_logs_them_and_undo_removes_them()
    {
        var parent = await h.Gmail.Inner.CreateLabelAsync("Types", Ct);
        var receipt = await h.Gmail.Inner.CreateLabelAsync("Types/Receipt", Ct);
        await SeedAsync(("a00", "New/Bills", false, false), ("a01", "New/Bills", false, false), ("a02", "New/Bills", false, false));
        await SetTypeAsync("a00", "Types/Invoice");
        await SetTypeAsync("a01", "Types/Receipt");
        await SetTypeAsync("a02", "Types/ not a path");
        var before = new[] { "a00", "a01", "a02" }.ToDictionary(id => id, Labels);

        var apply = await ApplyAndRunAsync();

        apply.MessageCount.ShouldBe(3);
        var ids = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        ids.Keys.Count(k => k.StartsWith("Types", StringComparison.OrdinalIgnoreCase)).ShouldBe(3);
        Labels("a00").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"], ids["Types/Invoice"]], ignoreOrder: true);
        Labels("a01").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"], receipt.Id], ignoreOrder: true);
        Labels("a02").ShouldBe(["CATEGORY_UPDATES", ids["New/Bills"]], ignoreOrder: true);
        ids.ShouldContainKeyAndValue("Types", parent.Id);
        (await DetailAsync(apply.Id)).CreatedLabels.Select(l => l.Name).ShouldBe(["New", "New/Bills", "Types/Invoice"], ignoreOrder: true);
        await using (var db = postgres.CreateDbContext())
        {
            var logs = await db.ActionLog.Where(l => l.BatchId == apply.Id).ToDictionaryAsync(l => l.MessageId, Ct);
            logs["a00"].LabelsAdded.ShouldBe(["New/Bills", "Types/Invoice"], ignoreOrder: true);
            logs["a01"].LabelsAdded.ShouldBe(["New/Bills", "Types/Receipt"], ignoreOrder: true);
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
    public async Task Apply_creates_a_nested_type_and_a_processor_merchant_topic_parent_first_in_one_call_and_undo_removes_them()
    {
        await h.Gmail.Inner.CreateLabelAsync("Finance", Ct);
        await h.Gmail.Inner.CreateLabelAsync("Finance/Payco", Ct);
        await h.Gmail.Inner.CreateLabelAsync("Types", Ct);
        await SeedAsync(("a00", "Finance/Payco/Gridco", false, false));
        await SetTypeAsync("a00", "Types/Utilities/Electricity");
        var before = Labels("a00");
        var calls = h.Gmail.BatchModifyCalls.Count;

        var apply = await ApplyAndRunAsync();

        var ids = (await h.Gmail.Inner.ListLabelsAsync(Ct)).ToDictionary(l => l.Name, l => l.Id);
        Labels("a00").ShouldBe(["CATEGORY_UPDATES", ids["Finance/Payco/Gridco"], ids["Types/Utilities/Electricity"]], ignoreOrder: true);
        h.Gmail.BatchModifyCalls.Skip(calls).ShouldHaveSingleItem().ShouldBe(["a00"]);
        (await DetailAsync(apply.Id)).CreatedLabels.Select(l => l.Name)
            .ShouldBe(["Finance/Payco/Gridco", "Types/Utilities", "Types/Utilities/Electricity"], ignoreOrder: true);
        await using (var db = postgres.CreateDbContext())
        {
            (await db.ActionLog.SingleAsync(l => l.BatchId == apply.Id, Ct)).LabelsAdded
                .ShouldBe(["Finance/Payco/Gridco", "Types/Utilities/Electricity"], ignoreOrder: true);
        }

        await UndoAsync(apply.Id);
        await h.RunNextAsync();

        Labels("a00").ShouldBe(before, ignoreOrder: true);
    }

    private async Task SetTypeAsync(string id, string type)
    {
        await using var db = postgres.CreateDbContext();
        await db.Suggestions.Where(s => s.MessageId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DocumentTypeLabel, type), Ct);
    }
}
