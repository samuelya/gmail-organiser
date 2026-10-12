using GmailOrganiser.Analysis;
using GmailOrganiser.Review;
using GmailOrganiser.Rules;

namespace GmailOrganiser.Tests.Unit.Rules;

/// <summary>The pattern filter proposal's labels (#531): additional labels come only with a topic that isn't the delete label.</summary>
public sealed class FilterProposalQueryTests
{
    private const string Address = "shop@example.com";
    private const string DeleteLabel = "Synthetic Delete";
    private static readonly string[] Extras = ["Synthetic/Extra A", "Synthetic/Extra B"];

    [Fact]
    public void A_topic_filter_adds_the_extras_after_the_topic()
    {
        var suggested = FilterProposalQuery.Suggest(Address, Pattern("Synthetic/Offers", toBeDeleted: false), DeleteLabel, Allowlist.Empty);

        suggested.Action.AddLabelNames.ShouldBe(["Synthetic/Offers", .. Extras]);
    }

    [Fact]
    public void A_delete_filter_and_an_allowlisted_sender_whose_topic_is_the_delete_label_add_no_extras()
    {
        var delete = FilterProposalQuery.Suggest(Address, Pattern("Synthetic/Offers", toBeDeleted: true), DeleteLabel, Allowlist.Empty);
        var allowlist = new Allowlist(new HashSet<string> { Address }, []);
        var protectedSender = FilterProposalQuery.Suggest(Address, Pattern(" synthetic delete", toBeDeleted: true), DeleteLabel, allowlist);

        delete.Action.AddLabelNames.ShouldBe([DeleteLabel]);
        protectedSender.Action.AddLabelNames.ShouldBe([" synthetic delete"]);
    }

    private static SenderPatternDto Pattern(string topic, bool toBeDeleted) => new(topic, false, toBeDeleted, 2, 1, 0, null, Extras);
}
