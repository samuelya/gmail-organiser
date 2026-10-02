using GmailOrganiser.Analysis.Prompts;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class SuggestionOutputParserTests
{
    private static readonly HashSet<string> Ids = ["m1", "m2"];

    private static string Item(string id, string label = "Topic/Sub", string confidence = "0.9", string extra = "") =>
        $$"""{"id":"{{id}}","topicLabel":"{{label}}","isNewLabel":false,"needsAction":true,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":{{confidence}},"reason":"Synthetic reason"{{extra}}}""";

    private static string Both => $"[{Item("m1")},{Item("m2")}]";

    [Fact]
    public void Valid_array_parses_every_email()
    {
        var (valid, errors, filter) = SuggestionOutputParser.Parse(Both, Ids);

        errors.ShouldBeEmpty();
        filter.ShouldBeNull();
        valid.Select(v => v.Id).ShouldBe(["m1", "m2"]);
        valid[0].ShouldBe(new SuggestionOutput("m1", "Topic/Sub", false, true, false, false, 0.9, "Synthetic reason"));
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

    [Fact]
    public void Empty_filter_criteria_is_an_error_without_dropping_the_suggestion()
    {
        var withFilter = Item("m1", extra: ""","filterCriteria":{"from":"","listId":null}""");

        var result = SuggestionOutputParser.Parse($"[{withFilter}]", new HashSet<string> { "m1" });

        result.Filter.ShouldBeNull();
        result.Valid.ShouldHaveSingleItem();
        result.Errors.ShouldHaveSingleItem().ShouldContain("filterCriteria");
    }
}
