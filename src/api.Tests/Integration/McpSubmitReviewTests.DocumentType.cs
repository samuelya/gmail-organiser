using System.Net;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary><c>submit_review</c>'s <c>document_type_label</c> and accepting it (#245).</summary>
public sealed partial class McpSubmitReviewTests
{
    [Theory]
    [InlineData(null, "Synthetic Types/Power", "Finance/Invoices", "Document-type labels are off")]
    [InlineData("Synthetic Types", "Synthetic Types/Power/Deep", "Finance/Invoices", "exactly one level")]
    [InlineData("Synthetic Types", "Other/Power", "Finance/Invoices", "exactly one level")]
    [InlineData("Synthetic Types", "INBOX/Power", "Finance/Invoices", "Gmail accepts")]
    [InlineData("Synthetic Types", "Synthetic Types/Power", "synthetic types/power", "differ from the topic")]
    public async Task Alternative_document_type_is_checked_as_the_review_edit_checks_it(
        string? parent, string type, string topic, string reason)
    {
        await ParentAsync(parent);

        Failed(await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = "alternative",
            ["topic_label"] = topic,
            ["document_type_label"] = type,
            ["reasoning"] = "Synthetic reasoning.",
        }), "queued", reason);
        (await RowAsync(singleItem)).Verdict.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, false, null, "Synthetic Types/Gas")]
    [InlineData("", true, null, null)]
    [InlineData(" synthetic types/Power ", true, "Synthetic Types/Power", "Synthetic Types/Power")]
    [InlineData("Power", true, "Synthetic Types/Power", "Synthetic Types/Power")]
    public async Task Alternative_keeps_clears_or_sets_the_document_type_and_accepting_applies_it(
        string? type, bool set, string? stored, string? expected)
    {
        await ParentAsync("Synthetic Types");
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "c00", SuggestionStatus.Pending, s => s.DocumentTypeLabel = "Synthetic Types/Gas");
        }

        var args = new Dictionary<string, object?>
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = "alternative",
            ["topic_label"] = "Finance/Invoices",
            ["reasoning"] = "Synthetic reasoning.",
        };
        if (type is not null)
        {
            args["document_type_label"] = type;
        }

        Ok(await SubmitAsync(args));
        var row = await RowAsync(singleItem);
        (row.VerdictDocumentTypeSet, row.VerdictDocumentTypeLabel).ShouldBe((set, stored));

        (await AcceptAsync(singleItem)).VerdictDocumentTypeLabel.ShouldBe(stored);
        await using var check = postgres.CreateDbContext();
        var suggestion = await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == c00, Ct);
        (suggestion.Status, suggestion.TopicLabel, suggestion.DocumentTypeLabel).ShouldBe((SuggestionStatus.Approved, "Finance/Invoices", expected));
    }

    [Fact]
    public async Task Agree_on_a_group_stores_the_card_document_type_and_accepting_approves_its_typed_members()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop && s.MessageId != "a09")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DocumentTypeLabel, "Synthetic Types/Power"), Ct);
        }

        Ok(await SubmitAsync(new() { ["id"] = groupItem.ToString(), ["verdict"] = "agree", ["reasoning"] = "Synthetic reasoning." }));
        (await RowAsync(groupItem)).VerdictDocumentTypeLabel.ShouldBe("Synthetic Types/Power");

        (await AcceptAsync(groupItem)).VerdictDocumentTypeLabel.ShouldBe("Synthetic Types/Power");
        await using var check = postgres.CreateDbContext();
        var statuses = await check.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop)
            .ToDictionaryAsync(s => s.MessageId, s => s.Status, Ct);
        statuses["a09"].ShouldBe(SuggestionStatus.Pending);
        statuses.Where(p => p.Key != "a09").ShouldAllBe(p => p.Value == SuggestionStatus.Approved);
    }

    [Theory]
    [InlineData("agree")]
    [InlineData("needs_human")]
    public async Task Document_type_on_a_verdict_other_than_alternative_is_refused(string verdict)
    {
        Failed(await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = verdict,
            ["document_type_label"] = "Synthetic Types/Power",
            ["reasoning"] = "Synthetic reasoning.",
        }), "queued", "only for 'alternative'");
        (await RowAsync(singleItem)).Verdict.ShouldBeNull();
    }

    [Fact]
    public async Task Alternative_on_a_group_without_a_document_type_keeps_each_member_type()
    {
        // Mixed types and one member without: the card shows the most common, which accepting must not spread.
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop && string.Compare(s.MessageId, "a05") < 0)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DocumentTypeLabel, "Synthetic Types/Power"), Ct);
            await db.Suggestions.Where(s => s.SenderAddress == AnalysisRunHarness.Shop && string.Compare(s.MessageId, "a05") >= 0 && s.MessageId != "a09")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DocumentTypeLabel, "Synthetic Types/Gas"), Ct);
        }

        var before = await TypesAsync();
        Ok(await SubmitAsync(new()
        {
            ["id"] = groupItem.ToString(),
            ["verdict"] = "alternative",
            ["topic_label"] = "Finance/Invoices",
            ["reasoning"] = "Synthetic reasoning.",
        }));
        (await RowAsync(groupItem)).ShouldSatisfyAllConditions(
            r => r.VerdictDocumentTypeSet.ShouldBeFalse(), r => r.VerdictDocumentTypeLabel.ShouldBeNull());

        await AcceptAsync(groupItem);
        (await TypesAsync()).ShouldBe(before);
        before["a09"].ShouldBeNull();
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ToListAsync(Ct))
            .ShouldAllBe(s => s.Status == SuggestionStatus.Approved && s.TopicLabel == "Finance/Invoices" && !s.DocumentTypeIsNew);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Other Types")]
    public async Task Accepting_a_document_type_the_parent_setting_no_longer_allows_is_refused(string? newParent)
    {
        await ParentAsync("Synthetic Types");
        Ok(await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = "alternative",
            ["topic_label"] = "Finance/Invoices",
            ["document_type_label"] = "Synthetic Types/Power",
            ["reasoning"] = "Synthetic reasoning.",
        }));
        await ParentAsync(newParent);

        var response = await h.PostAsync($"/api/claude/reviews/{singleItem}/accept", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("Invalid document type");
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == c00, Ct)).Status.ShouldBe(SuggestionStatus.Pending);
        (await RowAsync(singleItem)).Resolution.ShouldBe(ExternalReviewResolution.None);
    }

    [Fact]
    public async Task Alternative_on_a_group_sets_the_document_type_on_every_pending_member()
    {
        await ParentAsync("Synthetic Types");

        Ok(await SubmitAsync(new()
        {
            ["id"] = groupItem.ToString(),
            ["verdict"] = "alternative",
            ["topic_label"] = "Finance/Invoices",
            ["document_type_label"] = "Synthetic Types/Receipts",
            ["reasoning"] = "Synthetic reasoning.",
        }));
        await AcceptAsync(groupItem);

        await using var check = postgres.CreateDbContext();
        var members = await check.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop).ToListAsync(Ct);
        members.Count.ShouldBe(10);
        members.ShouldAllBe(s => s.Status == SuggestionStatus.Approved && s.TopicLabel == "Finance/Invoices"
            && s.DocumentTypeLabel == "Synthetic Types/Receipts" && s.DocumentTypeIsNew);
    }

    private async Task ParentAsync(string? parent)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { DocumentTypeParent = parent }, Ct);
    }

    // Sorted by message ID: Shouldly compares dictionaries in enumeration order, and Postgres returns updated rows in any order.
    private async Task<SortedDictionary<string, string?>> TypesAsync()
    {
        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().Where(s => s.SenderAddress == AnalysisRunHarness.Shop)
            .ToDictionaryAsync(s => s.MessageId, s => s.DocumentTypeLabel, Ct);
        return new SortedDictionary<string, string?>(rows, StringComparer.Ordinal);
    }
}
