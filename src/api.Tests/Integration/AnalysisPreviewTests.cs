using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class AnalysisPreviewTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const int ShopCount = 1000;
    private const string Shop = "shop@example.com";
    private const string Filter = "filter@example.com";
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 1000 synthetic shop messages over 25 subject templates, plus six newer messages from an allowlisted sender that
    /// exercise the candidate filters (analysed, applied, deleted in Gmail, outside the inbox).
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Suggestions.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
        await db.Senders.ExecuteDeleteAsync(Ct);
        await db.Settings.ExecuteDeleteAsync(Ct);
        db.Messages.AddRange(Enumerable.Range(0, ShopCount).Select(i =>
            Message($"shop-{i:D4}", Shop, $"Offer {Word(i % 25)} number {i}", Start.AddMinutes(i))));
        db.Messages.AddRange(
            Message("f-normal-1", Filter, "Report 1", Start.AddDays(10)),
            Message("f-normal-2", Filter, "Report 2", Start.AddDays(11)),
            Message("f-normal-3", Filter, "Report 3", Start.AddDays(12)),
            Message("f-archived", Filter, "Report 4", Start.AddDays(13), labels: ["CATEGORY_UPDATES"]),
            Message("f-analysed", Filter, "Report 5", Start.AddDays(14), status: AnalysisStatus.Analysed),
            Message("f-applied", Filter, "Report 6", Start.AddDays(15), status: AnalysisStatus.Applied),
            Message("f-deleted", Filter, "Report 7", Start.AddDays(16), deleted: true));
        db.Senders.Add(new SenderRow { Address = Filter, Domain = "example.com", Allowlisted = true, UpdatedAt = Start });
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Word(int n) => $"{(char)('a' + n % 26)}{(char)('a' + n / 2 % 26)}{(char)('k' + n % 7)}";

    private static MessageRow Message(
        string id, string from, string subject, DateTimeOffset date,
        string[]? labels = null, AnalysisStatus status = AnalysisStatus.NotAnalysed, bool deleted = false) => new()
        {
            Id = id,
            ThreadId = id,
            FromAddress = from,
            Subject = subject,
            InternalDate = date,
            LabelIds = labels ?? ["INBOX", "CATEGORY_UPDATES"],
            Category = MessageCategory.Updates,
            AnalysisStatus = status,
            DeletedInGmail = deleted,
            FetchedAt = Start,
            UpdatedAt = Start,
        };

    [Fact]
    public async Task Candidates_apply_the_scope_filters_newest_first()
    {
        await using var db = postgres.CreateDbContext();

        (await AnalysisCandidates.QueryAsync(db, AnalysisScope.Inbox, null, null, 4, Ct)).Select(m => m.Id)
            .ShouldBe(["f-normal-3", "f-normal-2", "f-normal-1", "shop-0999"]);
        (await AnalysisCandidates.QueryAsync(db, AnalysisScope.All, null, null, 2, Ct)).Select(m => m.Id)
            .ShouldBe(["f-archived", "f-normal-3"]);
        (await AnalysisCandidates.QueryAsync(db, AnalysisScope.Sender, " FILTER@example.com ", null, 50, Ct)).Select(m => m.Id)
            .ShouldBe(["f-archived", "f-normal-3", "f-normal-2", "f-normal-1"]);
        (await AnalysisCandidates.QueryAsync(
                db, AnalysisScope.Messages, null, ["f-applied", "f-analysed", "f-deleted", "shop-0001", "missing"], 50, Ct))
            .Select(m => m.Id).ShouldBe(["f-analysed", "shop-0001"]);
    }

    [Fact]
    public async Task Preview_of_1000_inbox_candidates_groups_without_model_calls_within_the_bound()
    {
        var watch = Stopwatch.StartNew();
        var preview = await PreviewAsync(new { scope = "inbox", count = 1000 });
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        preview.Messages.ShouldBe(1000);
        // 3 allowlisted filter messages + 997 shop messages over 25 templates.
        preview.Groups.ShouldBe(26);
        preview.EstimatedLlmCalls.ShouldBe(preview.Groups);
        preview.EstimatedDerived.ShouldBe(997 - 25 * 3);
        preview.EstimatedFromMemory.ShouldBe(0);
        preview.EmbeddingsAvailable.ShouldBeFalse();
        preview.LargestGroups.Count.ShouldBe(10);
        preview.LargestGroups.ShouldAllBe(g => g.SenderAddress == Shop && g.Size == 40 && g.Representatives == 3);
        preview.LargestGroups[0].Key.ShouldStartWith($"from:{Shop}|updates|offer ");
    }

    [Fact]
    public async Task Preview_defaults_the_count_and_makes_allowlisted_members_representatives()
    {
        (await PreviewAsync(new { scope = "inbox" })).Messages.ShouldBe(20);

        var preview = await PreviewAsync(new { scope = "sender", senderAddress = Filter });

        preview.Messages.ShouldBe(4);
        preview.Groups.ShouldBe(1);
        preview.EstimatedDerived.ShouldBe(0);
        preview.LargestGroups.ShouldHaveSingleItem().Representatives.ShouldBe(4);
        preview.LargestGroups[0].Display.ShouldBe("Report 4");
    }

    [Theory]
    [InlineData("""{"scope":"bogus"}""")]
    [InlineData("""{"scope":"inbox","count":0}""")]
    [InlineData("""{"scope":"inbox","count":1001}""")]
    [InlineData("""{"scope":"sender"}""")]
    [InlineData("""{"scope":"messages"}""")]
    [InlineData("""{"scope":"messages","messageIds":[""]}""")]
    [InlineData("""{}""")]
    public async Task Invalid_requests_are_400(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await Client().PostAsync("/api/analysis/preview", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    private async Task<GroupingPreviewDto> PreviewAsync(object request)
    {
        var response = await Client().PostAsJsonAsync("/api/analysis/preview", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GroupingPreviewDto>(Ct)).ShouldNotBeNull();
    }
}
