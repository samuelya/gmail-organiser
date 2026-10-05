using System.Net;
using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>An <c>alternative</c> may not make the delete label the topic of an email not to be deleted (#401).</summary>
public sealed partial class McpSubmitReviewTests
{
    private const string DeleteLabel = "Synthetic Delete";

    [Theory]
    [InlineData(DeleteLabel, null)]
    [InlineData(" synthetic delete ", false)]
    public async Task Alternative_with_the_delete_label_as_topic_not_to_be_deleted_is_refused(string topic, bool? toBeDeleted)
    {
        await DeleteLabelAsync(DeleteLabel);

        Failed(await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = "alternative",
            ["topic_label"] = topic,
            ["to_be_deleted"] = toBeDeleted,
            ["reasoning"] = "Synthetic reasoning.",
        }), "queued", "The delete label is only the topic of an email to be deleted.");
        (await RowAsync(singleItem)).Verdict.ShouldBeNull();
        notifier.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Alternative_with_the_delete_label_as_topic_to_be_deleted_is_stored()
    {
        await DeleteLabelAsync(DeleteLabel);

        Ok(await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = "alternative",
            ["topic_label"] = DeleteLabel,
            ["to_be_deleted"] = true,
            ["reasoning"] = "Synthetic reasoning.",
        }));
        (await RowAsync(singleItem)).VerdictTopicLabel.ShouldBe(DeleteLabel);
    }

    [Fact]
    public async Task Accepting_an_alternative_whose_topic_became_the_delete_label_is_refused()
    {
        Ok(await SubmitAsync(new()
        {
            ["id"] = singleItem.ToString(),
            ["verdict"] = "alternative",
            ["topic_label"] = "Synthetic Archive",
            ["to_be_deleted"] = false,
            ["reasoning"] = "Synthetic reasoning.",
        }));
        await DeleteLabelAsync("Synthetic Archive");

        var response = await h.PostAsync($"/api/claude/reviews/{singleItem}/accept", new { });
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("delete label");
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == c00, Ct)).Status.ShouldBe(SuggestionStatus.Pending);
        (await RowAsync(singleItem)).Resolution.ShouldBe(ExternalReviewResolution.None);
    }

    private async Task DeleteLabelAsync(string name)
    {
        await using var scope = h.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { DeleteLabelName = name }, Ct);
    }
}
