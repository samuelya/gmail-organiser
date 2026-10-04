using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Rules;
using GmailOrganiser.Tests.Fakes;
using Google.Apis.Gmail.v1.Data;

namespace GmailOrganiser.Tests.Unit;

public sealed class FilterMappingTests
{
    private static readonly GmailFilterCriteria FullCriteria = new(
        "news@example.com", "me@example.com", "Synthetic offer", "list:example.com", "label:keep", true, true, 2048,
        GmailSizeComparison.Smaller);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Criteria_and_action_round_trip_through_the_google_filter()
    {
        var action = new GmailFilterAction(["Label_1", "IMPORTANT"], ["INBOX", "UNREAD"]);

        var google = GoogleGmailClient.ToGoogleFilter(FullCriteria, action);
        google.Id = "f-1";
        var mapped = GoogleGmailClient.ToGmailFilter(google);

        mapped.Id.ShouldBe("f-1");
        mapped.Criteria.ShouldBe(FullCriteria);
        mapped.Action.AddLabelIds.ShouldBe(["Label_1", "IMPORTANT"]);
        mapped.Action.RemoveLabelIds.ShouldBe(["INBOX", "UNREAD"]);
        mapped.Action.Forward.ShouldBeNull();
        google.Criteria.SizeComparison.ShouldBe("smaller");
    }

    [Fact]
    public void Absent_parts_of_a_google_filter_read_as_null_and_forward_is_kept()
    {
        var filter = new Filter
        {
            Id = "f-2",
            Criteria = new FilterCriteria { From = "", Query = "from:example.com", SizeComparison = "unspecified" },
            Action = new FilterAction { Forward = "archive@example.com" },
        };

        var mapped = GoogleGmailClient.ToGmailFilter(filter);

        mapped.Criteria.ShouldBe(new GmailFilterCriteria(Query: "from:example.com"));
        mapped.Action.AddLabelIds.ShouldBeEmpty();
        mapped.Action.RemoveLabelIds.ShouldBeEmpty();
        mapped.Action.Forward.ShouldBe("archive@example.com");
    }

    [Fact]
    public void Stored_json_round_trips_the_criteria_and_action()
    {
        var action = new GmailFilterAction(["Label_2"], ["INBOX"]);

        var row = new FilterRow { Criteria = FilterRow.WriteCriteria(FullCriteria), Action = FilterRow.WriteAction(action) };

        row.ReadCriteria().ShouldBe(FullCriteria);
        row.ReadAction().AddLabelIds.ShouldBe(["Label_2"]);
        row.ReadAction().RemoveLabelIds.ShouldBe(["INBOX"]);
        row.Criteria.ShouldContain("\"sizeComparison\":\"smaller\"");
        row.Criteria.ShouldNotContain("null");
    }

    [Fact]
    public void Summary_lists_the_present_parts_in_a_fixed_order()
    {
        var all = FullCriteria with { SizeComparison = GmailSizeComparison.Larger };

        FilterSnapshot.Summarise(all).ShouldBe(
            "from:news@example.com to:me@example.com subject:\"Synthetic offer\" has:attachment larger:2048 -(label:keep) (list:example.com)");
        FilterSnapshot.Summarise(new GmailFilterCriteria(Query: "is:chat")).ShouldBe("(is:chat)");
        FilterSnapshot.Summarise(new GmailFilterCriteria(HasAttachment: false, Size: 10)).ShouldBe("size:10");
        FilterSnapshot.Summarise(new GmailFilterCriteria()).ShouldBe("");
    }

    [Fact]
    public void Dto_resolves_label_names_and_derives_the_action_flags()
    {
        var row = new FilterRow
        {
            Id = "f-3",
            Criteria = FilterRow.WriteCriteria(new GmailFilterCriteria(From: "a@example.com")),
            Action = FilterRow.WriteAction(new GmailFilterAction(["Label_1", "Label_gone"], ["INBOX", "UNREAD"], "b@example.com")),
            CriteriaSummary = "from:a@example.com",
        };

        var dto = FilterSnapshot.ToDto(row, new Dictionary<string, string> { ["Label_1"] = "Example" });

        dto.Action.AddLabels.ShouldBe([new LabelRefDto("Label_1", "Example"), new LabelRefDto("Label_gone", null)]);
        dto.Action.SkipInbox.ShouldBeTrue();
        dto.Action.MarkRead.ShouldBeTrue();
        dto.Action.Forwards.ShouldBeTrue();
        dto.Criteria.From.ShouldBe("a@example.com");
    }

    [Fact]
    public void Creating_a_forwarding_or_empty_filter_is_refused()
    {
        var label = new GmailFilterAction(["Label_1"], []);

        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(new GmailFilterCriteria(), label));
        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(
            new GmailFilterCriteria(From: "a@example.com"), label with { Forward = "b@example.com" }));
        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(
            new GmailFilterCriteria(From: "a@example.com"), new GmailFilterAction([], [])));
    }

    [Fact]
    public void Creating_a_filter_gmail_would_reject_is_refused()
    {
        var label = new GmailFilterAction(["Label_1"], []);

        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(new GmailFilterCriteria(ExcludeChats: true), label));
        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(new GmailFilterCriteria(Size: 1000), label));
        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(
            new GmailFilterCriteria(From: "a@example.com", SizeComparison: GmailSizeComparison.Larger), label));
        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(
            new GmailFilterCriteria(Size: -1, SizeComparison: GmailSizeComparison.Larger), label));
        Should.NotThrow(() => GmailFilter.EnsureValidCreate(
            new GmailFilterCriteria(ExcludeChats: true, Size: 1000, SizeComparison: GmailSizeComparison.Larger), label));
    }

    [Theory]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("subject")]
    [InlineData("query")]
    [InlineData("negatedQuery")]
    public void A_whitespace_only_criterion_is_no_criterion(string field)
    {
        var criteria = field switch
        {
            "from" => new GmailFilterCriteria(From: "   "),
            "to" => new GmailFilterCriteria(To: "\t"),
            "subject" => new GmailFilterCriteria(Subject: " "),
            "query" => new GmailFilterCriteria(Query: "  "),
            _ => new GmailFilterCriteria(NegatedQuery: " \n"),
        };

        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(criteria, new GmailFilterAction(["Label_1"], [])));
    }

    [Fact]
    public void A_blank_label_id_is_refused()
    {
        var criteria = new GmailFilterCriteria(From: "a@example.com");

        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(criteria, new GmailFilterAction([" "], [])));
        Should.Throw<ArgumentException>(() => GmailFilter.EnsureValidCreate(criteria, new GmailFilterAction(["Label_1"], [""])));
    }

    [Fact]
    public void Size_comparison_is_stored_as_gmails_string_and_read_back()
    {
        var criteria = new GmailFilterCriteria(Size: 10, SizeComparison: GmailSizeComparison.Smaller);

        var json = FilterRow.WriteCriteria(criteria);

        json.ShouldContain("\"sizeComparison\":\"smaller\"");
        new FilterRow { Criteria = json }.ReadCriteria().ShouldBe(criteria);
    }

    [Fact]
    public async Task The_fake_creates_lists_and_deletes_filters_and_a_missing_id_is_not_an_error()
    {
        var gmail = new FakeGmailClient(new FakeTokenStore(TimeProvider.System), []);

        var created = await gmail.CreateFilterAsync(new GmailFilterCriteria(From: "c@example.com"), new GmailFilterAction(["Label_1"], []), Ct);
        (await gmail.ListFiltersAsync(Ct)).Count.ShouldBe(FakeFilterStore.Seed.Count + 1);
        await gmail.DeleteFilterAsync(created.Id, Ct);
        await gmail.DeleteFilterAsync(created.Id, Ct);

        (await gmail.ListFiltersAsync(Ct)).ShouldBe(FakeFilterStore.Seed);
    }
}
