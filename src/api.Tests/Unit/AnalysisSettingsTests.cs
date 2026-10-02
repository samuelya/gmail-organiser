using System.Text.Json;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

public sealed class AnalysisSettingsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(AnalysisGroupingMode.Off, "\"off\"")]
    [InlineData(AnalysisGroupingMode.SenderSubject, "\"sender_subject\"")]
    [InlineData(AnalysisGroupingMode.Auto, "\"auto\"")]
    public void Grouping_mode_round_trips_as_snake_case(AnalysisGroupingMode mode, string json)
    {
        JsonSerializer.Serialize(mode, Json).ShouldBe(json);
        JsonSerializer.Deserialize<AnalysisGroupingMode>(json, Json).ShouldBe(mode);
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("2")]
    public void Unknown_grouping_mode_is_rejected(string json)
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<AnalysisGroupingMode>(json, Json));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_doubles_are_rejected(double value)
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null,
            AnalysisDerivedConfidencePenalty: value, AnalysisClusterDistance: value, BulkApproveThreshold: value));

        errors.Keys.ShouldBe(["analysisDerivedConfidencePenalty", "analysisClusterDistance", "bulkApproveThreshold"], ignoreOrder: true);
    }

    [Fact]
    public void Undefined_grouping_mode_is_rejected()
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null,
            AnalysisGroupingMode: (AnalysisGroupingMode)42));

        errors.Keys.ShouldBe(["analysisGroupingMode"]);
    }

    [Theory]
    [InlineData("Classify these.", true)]
    [InlineData("Classify these:\n{{emails}}", false)]
    [InlineData("   ", false)]
    public void Prompt_override_needs_the_emails_placeholder(string template, bool rejected)
    {
        var errors = SettingsValidation.Validate(new UpdateSettingsRequest(null, null, null, null, AnalysisPromptTemplate: template));

        errors.ContainsKey("analysisPromptTemplate").ShouldBe(rejected);
    }
}
