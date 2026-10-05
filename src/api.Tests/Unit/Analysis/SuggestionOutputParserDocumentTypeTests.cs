using GmailOrganiser.Analysis.Prompts;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class SuggestionOutputParserDocumentTypeTests
{
    private const string Parent = "Types";
    private static readonly HashSet<string> Ids = ["m1"];

    private static string Item(string documentType, string label = "Topic/Sub") =>
        $$"""[{"id":"m1","topicLabel":"{{label}}","isNewLabel":false,"needsAction":false,"toBeDeleted":false,"unsubscribeSuggested":false,"confidence":0.9,"reason":"Synthetic reason"{{documentType}}}]""";

    [Theory]
    [InlineData("", Parent, null)]
    [InlineData(",\"documentTypeLabel\":null", Parent, null)]
    [InlineData(",\"documentTypeLabel\":\"  \"", Parent, null)]
    [InlineData(",\"documentTypeLabel\":\"Types/Invoice\"", Parent, "Types/Invoice")]
    [InlineData(",\"documentTypeLabel\":\" types/Invoice \"", Parent, "Types/Invoice")]
    [InlineData(",\"documentTypeLabel\":\"TYPES/Receipt\"", " Types ", "Types/Receipt")]
    [InlineData(",\"documentTypeLabel\":\"types/Utilities/Electricity\"", Parent, "Types/Utilities/Electricity")]
    [InlineData(",\"documentTypeLabel\":\"Types/Utilities/Power/Peak\"", Parent, "Types/Utilities/Power/Peak")]
    [InlineData(",\"documentTypeLabel\":\"A/B/C/Kind/Sub\"", "A/B/C", "A/B/C/Kind/Sub")]
    [InlineData(",\"documentTypeLabel\":\"A/B/C/D/Kind\"", "A/B/C/D", "A/B/C/D/Kind")]
    [InlineData(",\"documentTypeLabel\":\"Types/Invoice\"", null, null)]
    [InlineData(",\"documentTypeLabel\":\"Types/Invoice\"", " ", null)]
    [InlineData(",\"documentTypeLabel\":42", null, null)]
    public void Accepted_or_silently_null(string documentType, string? parent, string? expected)
    {
        var result = SuggestionOutputParser.Parse(Item(documentType), Ids, documentTypeParent: parent);

        result.Errors.ShouldBeEmpty();
        result.Dropped.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().DocumentTypeLabel.ShouldBe(expected);
    }

    [Theory]
    [InlineData("42", "not a string")]
    [InlineData("\"Other/Invoice\"", "not 1 to 3 levels below the document-type parent")]
    [InlineData("\"Types\"", "not 1 to 3 levels below the document-type parent")]
    [InlineData("\"TypesInvoice\"", "not 1 to 3 levels below the document-type parent")]
    [InlineData("\"Types/Utilities/Power/Peak/Night\"", "not 1 to 3 levels below the document-type parent")]
    [InlineData("\"Types/ Invoice\"", "not a valid label path")]
    [InlineData("\"Types//Invoice\"", "not a valid label path")]
    [InlineData("\"types/sub\"", "same as topicLabel")]
    public void Invalid_value_is_dropped_with_a_note_and_the_suggestion_stays(string value, string reason)
    {
        var result = SuggestionOutputParser.Parse(Item($",\"documentTypeLabel\":{value}", label: "Types/Sub"), Ids, documentTypeParent: Parent);

        result.Errors.ShouldBeEmpty();
        result.Valid.ShouldHaveSingleItem().DocumentTypeLabel.ShouldBeNull();
        result.Dropped.ShouldHaveSingleItem().ShouldBe($"Email 'm1': 'documentTypeLabel' ignored ({reason}).");
    }

    [Theory]
    [InlineData("A/B/C", "A/B/C/Kind/Sub/Detail")]
    [InlineData("A/B/C/D", "A/B/C/D/Kind/Sub")]
    public void A_type_past_gmails_five_levels_is_dropped(string parent, string value)
    {
        var result = SuggestionOutputParser.Parse(Item($",\"documentTypeLabel\":\"{value}\""), Ids, documentTypeParent: parent);

        result.Valid.ShouldHaveSingleItem().DocumentTypeLabel.ShouldBeNull();
        result.Dropped.ShouldHaveSingleItem().ShouldBe("Email 'm1': 'documentTypeLabel' ignored (not a valid label path).");
    }

    [Fact]
    public void Too_deep_a_type_under_a_deep_parent_names_the_depth_left()
    {
        var result = SuggestionOutputParser.Parse(Item(",\"documentTypeLabel\":\"Other/Kind\""), Ids, documentTypeParent: "A/B/C/D");

        result.Dropped.ShouldHaveSingleItem().ShouldBe("Email 'm1': 'documentTypeLabel' ignored (not 1 level below the document-type parent).");
    }

    [Fact]
    public void A_type_past_gmails_length_limit_is_dropped()
    {
        var value = $"{Parent}/{new string('a', 100)}/{new string('b', 100)}/{new string('c', 30)}";

        var result = SuggestionOutputParser.Parse(Item($",\"documentTypeLabel\":\"{value}\""), Ids, documentTypeParent: Parent);

        result.Valid.ShouldHaveSingleItem().DocumentTypeLabel.ShouldBeNull();
        result.Dropped.ShouldHaveSingleItem().ShouldBe("Email 'm1': 'documentTypeLabel' ignored (not a valid label path).");
    }

    [Fact]
    public void Output_schema_declares_the_optional_field()
    {
        var schema = AnalysisPromptBuilder.CreateOptions().ResponseFormat.ShouldBeOfType<ChatResponseFormatJson>().Schema!.Value;
        var item = schema.GetProperty("properties").GetProperty("suggestions").GetProperty("items");

        item.GetProperty("properties").GetProperty("documentTypeLabel").GetProperty("type").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(["string", "null"]);
        item.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldNotContain("documentTypeLabel");
    }
}
