using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Fetch;
using GmailOrganiser.Review;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Similar pending (#560): the sender's other pending suggestions, across cards, whose outcome differs from the source's.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewSimilarPendingTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Bulk = "bulk@example.com";
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A normal run writes 20 suggestions: a00–a09 "Shopping" from one sender, in one card.</summary>
    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.SenderAddress == Bulk).ExecuteDeleteAsync(Ct);
            await db.Messages.Where(m => m.FromAddress == Bulk).ExecuteDeleteAsync(Ct);
            await db.Senders.Where(s => s.Address == Bulk).ExecuteDeleteAsync(Ct);
        }

        await h.DisposeAsync();
    }

    [Fact]
    public async Task Lists_the_senders_pending_suggestions_with_another_outcome_across_cards_newest_first()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await UpdateAsync(db, "a01", s => s.SetProperty(x => x.TopicLabel, "  shopping "));
            await UpdateAsync(db, "a02", s => s.SetProperty(x => x.TopicLabel, "Other"));
            await UpdateAsync(db, "a03", s => s.SetProperty(x => x.ToBeDeleted, true));
            await UpdateAsync(db, "a04", s => s.SetProperty(x => x.DocumentTypeLabel, "Invoice"));
            await UpdateAsync(db, "a05", s => s.SetProperty(x => x.NeedsAction, true));
            await UpdateAsync(db, "a06", s => s.SetProperty(x => x.TopicLabel, "Other").SetProperty(x => x.GroupKey, "another-card"));
            await UpdateAsync(db, "a07", s => s.SetProperty(x => x.TopicLabel, "Other"));
            await AnalysisRunHarness.DecideAsync(db, "a07", SuggestionStatus.Approved);
            await UpdateAsync(db, "a08", s => s.SetProperty(x => x.TopicLabel, "Other"));
            await db.Messages.Where(m => m.Id == "a08").ExecuteUpdateAsync(s => s.SetProperty(m => m.InternalDate, m => m.InternalDate.AddDays(1)), Ct);
            var a08 = await db.Suggestions.SingleAsync(s => s.MessageId == "a08", Ct);
            db.SuggestionAlternatives.Add(new SuggestionAlternativeRow
            {
                Id = Guid.CreateVersion7(),
                SuggestionId = a08.Id,
                MessageId = "a08",
                TopicLabel = "Compared",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await UpdateAsync(db, "b00", s => s.SetProperty(x => x.TopicLabel, "Other"));
            await db.SaveChangesAsync(Ct);
        }

        var source = (await IdsAsync("a00"))[0];
        var similar = await SimilarAsync(source);

        similar.SourceId.ShouldBe(source);
        similar.SuggestionIds.ShouldBe(await IdsAsync("a08", "a02", "a03", "a04", "a05", "a06"));
        (similar.Count, similar.WithAlternative, similar.Truncated).ShouldBe((6, 1, false));
    }

    [Fact]
    public async Task A_decided_source_still_lists_and_a_sender_without_differences_lists_nothing()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await AnalysisRunHarness.DecideAsync(db, "a00", SuggestionStatus.Applied);
            await UpdateAsync(db, "a00", s => s.SetProperty(x => x.TopicLabel, "Other"));
        }

        var applied = await SimilarAsync((await IdsAsync("a00"))[0]);
        (applied.Count, applied.SuggestionIds.Length, applied.Truncated).ShouldBe((9, 9, false));

        var none = await SimilarAsync((await IdsAsync("c00"))[0]);
        (none.Count, none.SuggestionIds.Length, none.WithAlternative, none.Truncated).ShouldBe((0, 0, 0, false));
    }

    [Fact]
    public async Task Outcome_equality_includes_extras_and_mail_type_and_ignores_document_type_case()
    {
        await using (var db = postgres.CreateDbContext())
        {
            await db.Suggestions.Where(s => s.MessageId.StartsWith("a0")).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.DocumentTypeLabel, "Invoice")
                .SetProperty(x => x.ExtraLabels, new[] { "Extra" })
                .SetProperty(x => x.MailType, MailType.Receipt), Ct);
            await UpdateAsync(db, "a01", s => s.SetProperty(x => x.DocumentTypeLabel, " invoice"));
            await UpdateAsync(db, "a02", s => s.SetProperty(x => x.ExtraLabels, new[] { "Extra", "More" }));
            await UpdateAsync(db, "a03", s => s.SetProperty(x => x.MailType, MailType.Personal));
            await UpdateAsync(db, "a04", s => s.SetProperty(x => x.ExtraLabels, Array.Empty<string>()));
        }

        var similar = await SimilarAsync((await IdsAsync("a00"))[0]);

        similar.SuggestionIds.ShouldBe(await IdsAsync("a02", "a03", "a04"));
    }

    [Fact]
    public async Task Unknown_source_is_not_found()
    {
        (await h.GetAsync($"/api/review/suggestions/{Guid.CreateVersion7()}/similar")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task More_than_the_cap_returns_the_newest_and_the_total()
    {
        var max = SettingsValidation.MaxAnalysisDefaultCount;
        var newest = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var ids = Enumerable.Range(0, max + 2).Select(i => $"bulk{i:D5}").ToList();
        await using (var db = postgres.CreateDbContext())
        {
            db.Senders.Add(new SenderRow { Address = Bulk, Domain = "example.com", TotalCount = ids.Count, UpdatedAt = newest });
            db.Messages.AddRange(ids.Select((id, i) => new MessageRow
            {
                Id = id,
                ThreadId = $"t-{id}",
                FromAddress = Bulk,
                InternalDate = newest.AddMinutes(-i),
                FetchedAt = newest,
                UpdatedAt = newest,
            }));
            db.Suggestions.AddRange(ids.Select((id, i) => new SuggestionRow
            {
                Id = Guid.CreateVersion7(),
                MessageId = id,
                SenderAddress = Bulk,
                Source = SuggestionSource.Llm,
                TopicLabel = i == 0 ? "Source" : "Other",
                Confidence = 0.9,
                Reason = "Synthetic reason",
                CreatedAt = newest,
            }));
            await db.SaveChangesAsync(Ct);
        }

        var similar = await SimilarAsync((await IdsAsync("bulk00000"))[0]);

        (similar.Count, similar.SuggestionIds.Length, similar.Truncated).ShouldBe((max + 1, max, true));
        similar.SuggestionIds[0].ShouldBe((await IdsAsync("bulk00001"))[0]);
        similar.SuggestionIds[^1].ShouldBe((await IdsAsync(ids[max]))[0]);
    }

    private static Task<int> UpdateAsync(
        Data.AppDbContext db, string messageId, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<SuggestionRow>> set) =>
        db.Suggestions.Where(s => s.MessageId == messageId).ExecuteUpdateAsync(set, Ct);

    private async Task<SimilarPendingDto> SimilarAsync(Guid id)
    {
        var response = await h.GetAsync($"/api/review/suggestions/{id}/similar");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SimilarPendingDto>(Ct)).ShouldNotBeNull();
    }

    /// <summary>The suggestion ids of <paramref name="messageIds"/>, in the given order.</summary>
    private async Task<Guid[]> IdsAsync(params string[] messageIds)
    {
        await using var db = postgres.CreateDbContext();
        var ids = await db.Suggestions.AsNoTracking().Where(s => messageIds.Contains(s.MessageId)).ToDictionaryAsync(s => s.MessageId, s => s.Id, Ct);
        return [.. messageIds.Select(m => ids[m])];
    }
}
