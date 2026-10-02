using System.Text.Json;
using GmailOrganiser.Analysis;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class AnalysisRunCursorTests
{
    // The job runner stores cursors with the web defaults (camelCase).
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    [Fact]
    public void Initial_cursor_holds_only_the_run_id()
    {
        var runId = Guid.CreateVersion7();

        var json = JsonSerializer.Serialize(new AnalysisRunCursor(runId), Json);
        var read = JsonSerializer.Deserialize<AnalysisRunCursor>(json, Json)!;

        json.ShouldContain("\"runId\"");
        (read.RunId, read.GroupsDone, read.LastGroupKey).ShouldBe((runId, 0, null));
        read.FailedIds.ShouldBeNull();
        read.IndividualIds.ShouldBeNull();
    }

    [Fact]
    public void Checkpointed_cursor_round_trips_its_id_lists()
    {
        var cursor = new AnalysisRunCursor(Guid.CreateVersion7(), 4, "from:shop@example.com|offer #|updates", ["m1", "m2"], ["m3"]);

        var read = JsonSerializer.Deserialize<AnalysisRunCursor>(JsonSerializer.Serialize(cursor, Json), Json)!;

        (read.RunId, read.GroupsDone, read.LastGroupKey).ShouldBe((cursor.RunId, 4, cursor.LastGroupKey));
        read.FailedIds.ShouldBe(["m1", "m2"]);
        read.IndividualIds.ShouldBe(["m3"]);
    }

    [Theory]
    [InlineData(3, 20, 0.85)]
    [InlineData(0, 0, 1.0)]
    [InlineData(4, 2, -1.0)]
    public void Saved_percent_is_one_minus_calls_per_covered_email(long calls, long covered, double expected) =>
        AnalysisRunService.SavedPercent(calls, covered).ShouldBe(expected, 1e-9);
}
