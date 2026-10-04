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
    [InlineData("\"Other/Invoice\"", "not one level below the document-type parent")]
    [InlineData("\"Types\"", "not one level below the document-type parent")]
    [InlineData("\"TypesInvoice\"", "not one level below the document-type parent")]
    [InlineData("\"Types/Invoice/Paid\"", "not one level below the document-type parent")]
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
