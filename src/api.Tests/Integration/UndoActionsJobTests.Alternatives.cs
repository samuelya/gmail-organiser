using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Analysis;
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

        Guid id;
        await using (var db = postgres.CreateDbContext())
        {
            var suggestion = await db.Suggestions.SingleAsync(s => s.MessageId == "a00", Ct);
            id = suggestion.Id;
            db.SuggestionAlternatives.Add(new SuggestionAlternativeRow
            {
                Id = Guid.CreateVersion7(),
                SuggestionId = id,
                MessageId = "a00",
                Source = SuggestionSource.Llm,
                TopicLabel = "Synthetic/Second",
                Confidence = 0.8,
                Reason = "Synthetic reason",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

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
