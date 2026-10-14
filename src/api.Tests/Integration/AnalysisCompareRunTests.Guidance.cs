using System.Net;
using System.Text.Json.Nodes;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The run's comment reaches the model as guidance in the user message (#561).</summary>
public sealed partial class AnalysisCompareRunTests
{
    [Fact]
    public async Task Comment_goes_to_the_user_message_and_a_blocked_label_it_names_still_does_not_get_through()
    {
        const string blocked = "Synthetic Blocked";
        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(s => s with { AnalysisBlockedLabels = [blocked] }, Ct);
        }

        var calls = h.Chat.Calls;
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Answer(ids, (id, _) => id.StartsWith('c') ? blocked : "Compared"));

        var run = await StartCompareAsync(new CompareRunRequest(null, first.Id, $"Use `{blocked}` for these.\nSecond line"));
        await h.RunNextAsync();

        (await h.GetRunAsync(run.Id)).Status.ShouldBe("completed");
        var prompts = h.Chat.Requests.Skip(calls).ToList();
        prompts.ShouldNotBeEmpty();
        prompts.ShouldAllBe(r => !r[0].Text.Contains(AnalysisPromptBuilder.GuidanceStart) && !r[0].Text.Contains("Second line"));
        prompts.ShouldAllBe(r => r[1].Text.Contains(
            $"{AnalysisPromptBuilder.GuidanceStart}\nUse `{blocked}` for these.\nSecond line\n{AnalysisPromptBuilder.GuidanceEnd}\n\n"
            + $"{AnalysisPromptBuilder.GuidedExtraLabelsInstruction}\n\nEmails to classify"));
        h.Chat.Requests.Take(calls).ShouldAllBe(r => !r[1].Text.Contains(AnalysisPromptBuilder.GuidanceStart));

        await using var db = postgres.CreateDbContext();
        var alternatives = await db.SuggestionAlternatives.AsNoTracking().Where(a => a.RunId == run.Id).ToListAsync(Ct);
        alternatives.ShouldNotBeEmpty();
        alternatives.ShouldAllBe(a => a.TopicLabel == "Compared" && a.PromptVersion == PromptTemplate.BuiltInVersion);
        alternatives.ShouldNotContain(a => a.MessageId.StartsWith('c'));
    }

    [Fact]
    public async Task An_existing_label_the_comment_names_reaches_the_alternative_and_accept_adds_it()
    {
        const string archive = "Synthetic Archive";
        await h.Gmail.Inner.CreateLabelAsync(archive, Ct);
        h.Services.GetRequiredService<LabelCatalog>().Invalidate();
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(
            WithExtras(AnalysisRunHarness.Answer(ids, (_, _) => "Compared"), archive.ToUpperInvariant(), "Synthetic Missing"));

        var run = await StartCompareAsync(new CompareRunRequest(null, first.Id, $"These also need {archive.ToLowerInvariant()} and Synthetic Missing."));
        await h.RunNextAsync();

        (await h.GetRunAsync(run.Id)).Status.ShouldBe("completed");
        Guid id;
        await using (var db = postgres.CreateDbContext())
        {
            var alternatives = await db.SuggestionAlternatives.AsNoTracking().Where(a => a.RunId == run.Id && a.Source == SuggestionSource.Llm).ToListAsync(Ct);
            alternatives.ShouldNotBeEmpty();
            alternatives.ShouldAllBe(a => a.ExtraLabels.SequenceEqual(new[] { archive }));
            alternatives.ShouldAllBe(a => a.Reason.EndsWith($" {ExtraLabelList.NotAddedNote} Synthetic Missing."), "the dropped extra is named on the card");
            id = alternatives[0].SuggestionId;
            await db.Suggestions.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.ExtraLabels, new[] { "Synthetic Kept" }), Ct);
        }

        var accept = await h.PostAsync("/api/review/alternatives/accept", new AlternativeDecisionRequest([id], null));

        accept.StatusCode.ShouldBe(HttpStatusCode.OK, await accept.Content.ReadAsStringAsync(Ct));
        await using var check = postgres.CreateDbContext();
        (await check.Suggestions.AsNoTracking().SingleAsync(s => s.Id == id, Ct)).ExtraLabels.ShouldBe([archive, "Synthetic Kept"]);
    }

    private static string WithExtras(string answer, params string[] extras)
    {
        var node = JsonNode.Parse(answer)!;
        foreach (var suggestion in node["suggestions"]!.AsArray())
        {
            suggestion!["extraLabels"] = new JsonArray([.. extras.Select(e => JsonValue.Create(e))]);
        }

        return node.ToJsonString();
    }

    [Fact]
    public async Task Compare_run_without_a_comment_sends_no_guidance()
    {
        var calls = h.Chat.Calls;
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(AnalysisRunHarness.Agree(ids));

        await StartCompareAsync(new CompareRunRequest(null, first.Id));
        await h.RunNextAsync();

        var prompts = h.Chat.Requests.Skip(calls).ToList();
        prompts.ShouldNotBeEmpty();
        prompts.ShouldAllBe(r => !r[1].Text.Contains(AnalysisPromptBuilder.GuidanceHeading));
    }
}
