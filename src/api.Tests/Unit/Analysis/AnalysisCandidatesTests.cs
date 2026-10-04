using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class AnalysisCandidatesTests
{
    private static readonly PersonalLabels Labels = PersonalLabels.From(
        [
            new GmailLabel("Label_1", "Topic/One", GmailLabelType.User),
            new GmailLabel("Label_7", "Synthetic Action", GmailLabelType.User),
            new GmailLabel("Label_8", "synthetic delete", GmailLabelType.User),
        ],
        new AppSettings { ActionLabelName = "Synthetic Action", DeleteLabelName = "Synthetic Delete" });

    private static MessageRow Msg(params string[] labels) => new() { Id = "m1", FromAddress = "shop@example.com", LabelIds = labels };

    [Fact]
    public void Labelled_eligibility_mirrors_the_query_on_personal_labels()
    {
        AnalysisCandidates.IsEligible(AnalysisScope.Labelled, Msg("INBOX", "Label_1"), Labels).ShouldBeTrue();
        AnalysisCandidates.IsEligible(AnalysisScope.Labelled, Msg("INBOX"), Labels).ShouldBeFalse();
        AnalysisCandidates.IsEligible(AnalysisScope.Labelled, Msg("Label_7", "Label_8"), Labels).ShouldBeFalse();
        AnalysisCandidates.IsEligible(AnalysisScope.Inbox, Msg("Label_7"), Labels).ShouldBeTrue();

        var analysed = Msg("Label_1");
        analysed.AnalysisStatus = AnalysisStatus.Analysed;
        AnalysisCandidates.IsEligible(AnalysisScope.Labelled, analysed, Labels).ShouldBeFalse();
    }

    [Fact]
    public void Personal_labels_leave_out_the_app_labels_by_name_case_insensitively()
    {
        Labels.AppLabelIds.ShouldBe(["Label_7", "Label_8"]);
        Labels.NamesOf(Msg("Label_8", "Label_1", "Label_7", "Label_42")).ShouldBe(["Topic/One"]);
        PersonalLabels.None.IsPersonal("Label_7").ShouldBeTrue();
    }

    [Fact]
    public void Review_group_title_names_the_label_set_it_was_split_by()
    {
        const string key = "from:shop@example.com|-|order # shipped|labels:Label_1,Label_5";

        ReviewQuery.GroupDisplay("Order 1 shipped", key, Labels.Names).ShouldBe("Order 1 shipped [Topic/One, Label_5]");
        ReviewQuery.GroupDisplay("Order 1 shipped", key).ShouldBe("Order 1 shipped");
    }
}
