using System.Net;
using GmailOrganiser.Analysis;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Additional labels (#531): a policy's suggestions carry its extras, apply adds them and undo removes them.</summary>
public sealed partial class PolicyApplyJobTests
{
    private const string ExtraA = "Example/Extra A";
    private const string ExtraB = "Example/Extra B";

    [Fact]
    public async Task A_policy_apply_adds_the_default_extras_and_a_changed_set_supersedes_its_own_suggestion()
    {
        // News b00..b05, archive by default with two extras. b02 and b03 hold this policy's approved suggestion from an
        // earlier apply: b02 with an older set of extras, b03 with the current one.
        var policy = await SeedPolicyAsync(AnalysisRunHarness.News, isMixed: false, PolicyAction.Archive);
        await using (var db = postgres.CreateDbContext())
        {
            await db.SenderPolicies.Where(p => p.Id == policy)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.ExtraLabels, new[] { ExtraA, ExtraB }), Ct);
        }

        await SeedOwnAsync("b02", policy, ["Example/Old"]);
        await SeedOwnAsync("b03", policy, [ExtraA, ExtraB]);

        (await h.PostWithoutBodyAsync($"/api/policies/{policy}/approve")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await h.RunNextAsync();
        await h.RunNextAsync();

        await using (var db = postgres.CreateDbContext())
        {
            var suggestions = await db.Suggestions.AsNoTracking().Where(s => s.PolicyId == policy).ToListAsync(Ct);
            suggestions.Count.ShouldBe(6);
            suggestions.ShouldAllBe(s => s.Status == SuggestionStatus.Applied && s.ExtraLabels.SequenceEqual(new[] { ExtraA, ExtraB }));

            // b02's set changed, so it was approved again and recorded; b03's outcome is unchanged and records nothing.
            var decided = await db.Decisions.Where(d => d.Source == SuggestionSource.Policy).OrderBy(d => d.MessageId).ToListAsync(Ct);
            decided.Select(d => d.MessageId).ShouldBe(["b00", "b01", "b02", "b04", "b05"]);
            decided.ShouldAllBe(d => d.ExtraLabels.SequenceEqual(new[] { ExtraA, ExtraB }));
        }

        (await LabelsAsync("b00")).ShouldBe([News, ExtraA, ExtraB], ignoreOrder: true);
        (await LabelsAsync("b02")).ShouldBe([News, ExtraA, ExtraB], ignoreOrder: true);

        (await h.PostAsync($"/api/history/{await BatchAsync(policy)}/undo", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await h.RunNextAsync();
        (await LabelsAsync("b00")).ShouldBe(["INBOX"], ignoreOrder: true);
    }

    /// <summary>This policy's approved suggestion for the default outcome, as an earlier apply left it.</summary>
    private Task SeedOwnAsync(string messageId, Guid policy, string[] extras) =>
        SeedSuggestionAsync(messageId, SuggestionStatus.Approved, s =>
        {
            (s.Source, s.PolicyId, s.TopicLabel, s.MailType, s.Confidence, s.Reason) =
                (SuggestionSource.Policy, policy, News, MailType.Newsletter, 0.8, "Policy: default");
            s.ExtraLabels = extras;
        });
}
