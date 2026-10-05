using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class SuggestionOutputParserTests
{
    private static readonly HashSet<string> Ids = ["m1", "m2"];

    private static string Item(string id, string label = "Topic/Sub", string confidence = "0.9", string extra = "") =>
        $$"""{"id":"{{id}}","topicLabel":"{{label}}","isNewLabel":false,"mailType":"notification","needsAction":true,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":{{confidence}},"reason":"Synthetic reason"{{extra}}}""";

    private static string Both => $"[{Item("m1")},{Item("m2")}]";

    [Fact]
    public void Valid_array_parses_every_email()
    {
        var (valid, errors, filter) = SuggestionOutputParser.Parse(
            Both, Ids, context: new SuggestionParseContext(new LabelTreeIndex(["Topic/Sub"]), []));

        errors.ShouldBeEmpty();
        filter.ShouldBeNull();
        valid.Select(v => v.Id).ShouldBe(["m1", "m2"]);
        valid[0].ShouldBe(new SuggestionOutput("m1", "Topic/Sub", false, true, false, false, 0.9, "Synthetic reason", MailType: MailType.Notification));
    }

    private static readonly Dictionary<string, IReadOnlyList<string>> Current = new()
    {
        ["m1"] = ["Subtopic", "Old/Topic", "Topic/Sub"],
    };

    [Fact]
    public void Replace_labels_keep_current_labels_trimmed_and_deduplicated()
    {
        var raw = $"[{Item("m1", label: "Topic/Subtopic", extra: ",\"replaceLabels\":[\" subtopic \",\"Subtopic\",\"Old/Topic\"]")},{Item("m2", extra: ",\"replaceLabels\":null")}]";

        var result = SuggestionOutputParser.Parse(raw, Ids, Current);

        result.Errors.ShouldBeEmpty();
        result.Dropped.ShouldBeEmpty();
        result.Valid[0].ReplaceLabels.ShouldBe(["Subtopic", "Old/Topic"]);
        result.Valid[1].ReplaceLabels.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\"Subtopic\"")]
    [InlineData("[3]")]
    [InlineData("[\"INBOX\"]")]
    [InlineData("[\"topic/sub\"]")]
    [InlineData("[\" \"]")]
    [InlineData("[\"Elsewhere\"]")]
    [InlineData("[\"Synthetic Delete\"]")]
    public void Unusable_replace_labels_are_dropped_without_failing_the_email(string value)
    {
        var raw = $"[{Item("m1", extra: $",\"replaceLabels\":{value}")},{Item("m2", extra: ",\"replaceLabels\":[\"Subtopic\"]")}]";

        var result = SuggestionOutputParser.Parse(raw, Ids, Current);

        result.Errors.ShouldBeEmpty();
        result.Valid.Select(v => v.Id).ShouldBe(["m1", "m2"]);
        result.Valid.ShouldAllBe(v => v.ReplaceLabels.Count == 0);
        result.Dropped.Count.ShouldBe(2);
        result.Dropped[0].ShouldStartWith("Email 'm1': ");
        result.Dropped[1].ShouldStartWith("Email 'm2': ");
    }

    [Fact]
    public void Replace_labels_drop_the_topic_label_but_keep_the_rest()
    {
        var raw = $"[{Item("m1", label: "Topic/Sub", extra: ",\"replaceLabels\":[\"Topic/Sub\",\"Old/Topic\"]")}]";

        var result = SuggestionOutputParser.Parse(raw, new HashSet<string> { "m1" }, Current);

        result.Valid.ShouldHaveSingleItem().ReplaceLabels.ShouldBe(["Old/Topic"]);
        result.Dropped.ShouldHaveSingleItem().ShouldStartWith("Email 'm1': 1 'replaceLabels' entry");
    }

    [Fact]
    public void Suggestions_object_with_a_top_level_filter_parses()
    {
        var raw = $$$"""{"suggestions":{{{Both}}},"filterCriteria":{"from":null,"listId":"list.example.com","subjectContains":null}}""";

        var result = SuggestionOutputParser.Parse(raw, Ids);

        result.Errors.ShouldBeEmpty();
        result.Valid.Select(v => v.Id).ShouldBe(["m1", "m2"]);
        result.Filter.ShouldBe(new FilterCriteriaOutput(null, "list.example.com", null));
    }

    [Fact]
    public void Trailing_prose_with_brackets_is_ignored()
    {
        var result = SuggestionOutputParser.Parse($"{Both}\nConfidence is in [0,1] as {{asked}}.", Ids);

        result.Errors.ShouldBeEmpty();
        result.Valid.Count.ShouldBe(2);
    }

    [Fact]
    public void Code_fences_are_stripped()
    {
        var result = SuggestionOutputParser.Parse($"```json\n{Both}\n```", Ids);

        result.Errors.ShouldBeEmpty();
        result.Valid.Count.ShouldBe(2);
    }

    [Fact]
    public void Leading_prose_is_skipped()
    {
        var result = SuggestionOutputParser.Parse($"Here are the suggestions you asked for:\n{Both}", Ids);

        result.Errors.ShouldBeEmpty();
        result.Valid.Count.ShouldBe(2);
    }

    [Fact]
    public void Bare_object_is_accepted_for_a_single_email()
    {
        var result = SuggestionOutputParser.Parse(Item("m1"), new HashSet<string> { "m1" });

        result.Errors.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().Id.ShouldBe("m1");
    }

    [Fact]
    public void Array_wrapped_in_an_object_is_accepted()
    {
        var result = SuggestionOutputParser.Parse($$$"""{"emails":{{{Both}}},"filterCriteria":{"from":"news@example.com"}}""", Ids);

        result.Errors.ShouldBeEmpty();
        result.Valid.Count.ShouldBe(2);
        result.Filter.ShouldBe(new FilterCriteriaOutput("news@example.com", null, null));
    }

    [Theory]
    [InlineData("1.005", 1.0)]
    [InlineData("-0.005", 0.0)]
    [InlineData("0", 0.0)]
    [InlineData("1", 1.0)]
    public void Confidence_within_tolerance_is_clamped(string confidence, double expected)
    {
        var result = SuggestionOutputParser.Parse($"[{Item("m1", confidence: confidence)}]", new HashSet<string> { "m1" });

        result.Errors.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().Confidence.ShouldBe(expected);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.2")]
    [InlineData("85")]
    [InlineData("\"0.9\"")]
    [InlineData("null")]
    [InlineData("1e400")]
    public void Bad_confidence_is_an_error(string confidence)
    {
        var result = SuggestionOutputParser.Parse($"[{Item("m1", confidence: confidence)}]", new HashSet<string> { "m1" });

        result.Valid.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().ShouldContain("'confidence'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Leading")]
    [InlineData("Trailing/")]
    [InlineData("A//B")]
    [InlineData("A/ B")]
    [InlineData("A/B/C/D/E/F")]
    [InlineData("Tab\\tInside")]
    [InlineData("TRASH")]
    [InlineData("spam")]
    [InlineData("INBOX")]
    [InlineData("Sent Mail")]
    public void Bad_label_is_an_error(string label)
    {
        var result = SuggestionOutputParser.Parse($"[{Item("m1", label: label)}]", new HashSet<string> { "m1" });

        result.Valid.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().ShouldContain("'topicLabel'");
    }

    [Fact]
    public void Label_length_limits_apply()
    {
        SuggestionOutputParser.IsValidLabelPath(new string('a', 100)).ShouldBeTrue();
        SuggestionOutputParser.IsValidLabelPath(new string('a', 101)).ShouldBeFalse();
        var five = string.Join('/', Enumerable.Repeat(new string('a', 44), 5));
        five.Length.ShouldBe(224);
        SuggestionOutputParser.IsValidLabelPath(five).ShouldBeTrue();
        SuggestionOutputParser.IsValidLabelPath(string.Join('/', Enumerable.Repeat(new string('a', 46), 5))).ShouldBeFalse();
    }

    [Fact]
    public void Booleans_are_strict()
    {
        var raw = Item("m1").Replace("\"needsAction\":true", "\"needsAction\":\"true\"", StringComparison.Ordinal);

        var result = SuggestionOutputParser.Parse($"[{raw}]", new HashSet<string> { "m1" });

        result.Valid.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().ShouldContain("'needsAction'");
    }

    [Fact]
    public void Unknown_id_is_an_error_and_the_rest_is_kept()
    {
        var result = SuggestionOutputParser.Parse($"[{Item("m1")},{Item("m2")},{Item("other")}]", Ids);

        result.Valid.Count.ShouldBe(2);
        result.Errors.ShouldHaveSingleItem().ShouldContain("Unknown id 'other'");
    }

    [Fact]
    public void Missing_id_is_reported()
    {
        var result = SuggestionOutputParser.Parse($"[{Item("m1")}]", Ids);

        result.Valid.ShouldHaveSingleItem().Id.ShouldBe("m1");
        result.Errors.ShouldBe(["Email 'm2': no answer."]);
    }

    [Fact]
    public void Duplicate_id_after_an_invalid_answer_is_used()
    {
        var result = SuggestionOutputParser.Parse($"[{Item("m1", confidence: "7")},{Item("m1", label: "Other")}]",
            new HashSet<string> { "m1" });

        result.Valid.ShouldHaveSingleItem().TopicLabel.ShouldBe("Other");
        result.Errors.ShouldHaveSingleItem().ShouldContain("'confidence'");
    }

    [Fact]
    public void Duplicate_id_keeps_the_first_answer()
    {
        var result = SuggestionOutputParser.Parse($"[{Item("m1")},{Item("m1", label: "Other")},{Item("m2")}]", Ids);

        result.Valid.Select(v => v.TopicLabel).ShouldBe(["Topic/Sub", "Topic/Sub"]);
        result.Errors.ShouldHaveSingleItem().ShouldContain("Duplicate id 'm1'");
    }

    [Theory]
    [InlineData("I could not classify these emails.")]
    [InlineData("[{\"id\":\"m1\",")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[1, \"two\"]")]
    public void Non_json_output_is_an_error_not_an_exception(string? raw)
    {
        var result = SuggestionOutputParser.Parse(raw, Ids);

        result.Valid.ShouldBeEmpty();
        result.Errors.ShouldContain("Email 'm1': no answer.");
        result.Errors.ShouldContain("Email 'm2': no answer.");
        result.Errors.Count.ShouldBeGreaterThan(2);
    }

    [Fact]
    public void Long_reason_is_cut_to_the_limit()
    {
        var raw = Item("m1").Replace("Synthetic reason", new string('r', 400), StringComparison.Ordinal);

        SuggestionOutputParser.Parse(raw, new HashSet<string> { "m1" }).Valid.ShouldHaveSingleItem()
            .Reason.Length.ShouldBe(SuggestionOutputParser.MaxReasonLength);
    }

    [Fact]
    public void Filter_criteria_is_read_once_from_an_item()
    {
        var withFilter = Item("m2", extra: ""","filterCriteria":{"from":" news@example.com ","listId":null,"subjectContains":"Weekly"}""");

        var result = SuggestionOutputParser.Parse($"[{Item("m1")},{withFilter}]", Ids);

        result.Errors.ShouldBeEmpty();
        result.Filter.ShouldBe(new FilterCriteriaOutput("news@example.com", null, "Weekly"));
    }

    [Theory]
    [InlineData("{\"from\":null,\"listId\":null,\"subjectContains\":null}")]
    [InlineData("{}")]
    public void All_null_filter_criteria_is_no_filter(string criteria)
    {
        var result = SuggestionOutputParser.Parse($$$"""{"suggestions":{{{Both}}},"filterCriteria":{{{criteria}}}}""", Ids);

        result.Filter.ShouldBeNull();
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Empty_filter_criteria_is_an_error_without_dropping_the_suggestion()
    {
        var withFilter = Item("m1", extra: ""","filterCriteria":{"from":"","listId":null}""");

        var result = SuggestionOutputParser.Parse($"[{withFilter}]", new HashSet<string> { "m1" });

        result.Filter.ShouldBeNull();
        result.Valid.ShouldHaveSingleItem();
        result.Errors.ShouldHaveSingleItem().ShouldContain("filterCriteria");
    }

    [Theory]
    [InlineData("""[{"id":"m1","topicLabel":"A\udc00","isNewLabel":false}]""")]
    [InlineData("""[{"id":"m1","topicLabel":"A","isNewLabel":false,"needsAction":false,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":0.5,"reason":"x\ud83d"}]""")]
    [InlineData("""[{"id":"\ud83d"}]""")]
    [InlineData("""[{"id":"m1","filterCriteria":{"from":"a\ud800@example.com"}}]""")]
    [InlineData("""[{"\ud800":1,"id":"m1"}]""")]
    [InlineData("""{"\ud800":1,"suggestions":[]}""")]
    [InlineData("""{"suggestions":[],"filterCriteria":{"from":"a\udfff@example.com"}}""")]
    public void Lone_surrogate_escape_is_an_error_not_an_exception(string raw)
    {
        var result = SuggestionOutputParser.Parse(raw, new HashSet<string> { "m1" });

        result.Valid.ShouldBeEmpty();
        result.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Lone_surrogate_in_one_item_keeps_the_others()
    {
        var raw = $$"""[{"id":"m1","reason":"x\ud83d"},{{Item("m2")}}]""";

        var result = SuggestionOutputParser.Parse(raw, Ids);

        result.Valid.ShouldHaveSingleItem().Id.ShouldBe("m2");
    }

    [Fact]
    public void Reason_is_cut_without_splitting_a_surrogate_pair()
    {
        var reason = new string('a', SuggestionOutputParser.MaxReasonLength - 1) + "\ud83d\ude00";
        var raw = $$"""{"id":"m1","topicLabel":"A","isNewLabel":false,"needsAction":false,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":0.5,"reason":"{{reason}}"}""";

        var result = SuggestionOutputParser.Parse(raw, new HashSet<string> { "m1" });

        var cut = result.Valid.ShouldHaveSingleItem().Reason;
        cut.Length.ShouldBe(SuggestionOutputParser.MaxReasonLength - 1);
        Should.NotThrow(() => new System.Text.UTF8Encoding(false, true).GetBytes(cut));
    }

    [Theory]
    [InlineData("Here are the [3] answers: ")]
    [InlineData("Answers {see below} [for both]: ")]
    [InlineData("[note] ")]
    public void Brackets_in_leading_prose_fall_through_to_the_json(string prose)
    {
        var result = SuggestionOutputParser.Parse(prose + Both, Ids);

        result.Errors.ShouldBeEmpty();
        result.Valid.Count.ShouldBe(2);
    }
}
