using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Common;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Claude review settings (#161): defaults, round-trip, ranges, the reviewer-mode converter and the token flag.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ClaudeReviewSettingsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string SyntheticToken = "synthetic-claude-token-value";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Defaults_when_no_row_exists()
    {
        var settings = await GetAsync(factory);

        settings.ClaudeReviewerMode.ShouldBe(ClaudeReviewerMode.Off);
        settings.ClaudeSuggestLowConfidence.ShouldBeFalse();
        settings.ClaudeSuggestThreshold.ShouldBe(0.60);
        settings.ClaudeSuggestNewLabels.ShouldBeFalse();
        settings.ClaudeRunTimeoutSeconds.ShouldBe(600);
        settings.ClaudeMaxItemsPerRun.ShouldBe(10);
        settings.ClaudeMaxTurns.ShouldBe(80);
        settings.ClaudeModel.ShouldBeNull();
        settings.ClaudeTokenSet.ShouldBeFalse();
        (await factory.CreateClient().GetStringAsync("/api/settings", Ct)).ShouldContain("\"claudeReviewerMode\":\"off\"");
    }

    [Fact]
    public async Task Settings_round_trip_and_are_kept_by_other_updates()
    {
        var response = await PutAsync(factory, new UpdateSettingsRequest(null, null, null, null,
            ClaudeReviewerMode: ClaudeReviewerMode.ClaudeDesktop, ClaudeSuggestLowConfidence: true, ClaudeSuggestThreshold: 0.75,
            ClaudeSuggestNewLabels: true, ClaudeRunTimeoutSeconds: 900, ClaudeMaxItemsPerRun: 25, ClaudeMaxTurns: 120,
            ClaudeModel: "  claude-model-a  "));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await PutAsync(factory, new UpdateSettingsRequest(null, "chat-model-a", null, null));

        var settings = await GetAsync(factory);
        settings.ClaudeReviewerMode.ShouldBe(ClaudeReviewerMode.ClaudeDesktop);
        settings.ClaudeSuggestLowConfidence.ShouldBeTrue();
        settings.ClaudeSuggestThreshold.ShouldBe(0.75);
        settings.ClaudeSuggestNewLabels.ShouldBeTrue();
        settings.ClaudeRunTimeoutSeconds.ShouldBe(900);
        settings.ClaudeMaxItemsPerRun.ShouldBe(25);
        settings.ClaudeMaxTurns.ShouldBe(120);
        settings.ClaudeModel.ShouldBe("claude-model-a");
    }

    [Fact]
    public async Task Empty_claude_model_clears_it()
    {
        await PutAsync(factory, new UpdateSettingsRequest(null, null, null, null, ClaudeModel: "claude-model-a"));

        var response = await PutAsync(factory, new UpdateSettingsRequest(null, null, null, null, ClaudeModel: " "));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(factory)).ClaudeModel.ShouldBeNull();
    }

    [Theory]
    [InlineData("headless_claude_code", ClaudeReviewerMode.HeadlessClaudeCode)]
    [InlineData("claude_desktop", ClaudeReviewerMode.ClaudeDesktop)]
    public async Task Reviewer_mode_is_snake_case_in_the_api_and_the_stored_document(string json, ClaudeReviewerMode mode)
    {
        var response = await PutJsonAsync($$"""{"claudeReviewerMode":"{{json}}"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain($"\"claudeReviewerMode\":\"{json}\"");
        (await GetAsync(factory)).ClaudeReviewerMode.ShouldBe(mode);
        await using var db = postgres.CreateDbContext();
        var stored = JsonNode.Parse((await db.Settings.SingleAsync(Ct)).Document).ShouldNotBeNull();
        stored["claudeReviewerMode"].ShouldNotBeNull().GetValue<string>().ShouldBe(json);
    }

    [Theory]
    [InlineData("\"claude\"")]
    [InlineData("1")]
    public async Task Unknown_reviewer_mode_gets_400(string value)
    {
        var response = await PutJsonAsync($$"""{"claudeReviewerMode":{{value}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync(factory)).ClaudeReviewerMode.ShouldBe(ClaudeReviewerMode.Off);
    }

    public static TheoryData<string, string> EdgeValues => new()
    {
        { "claudeSuggestThreshold", "0.30" }, { "claudeSuggestThreshold", "0.95" },
        { "claudeRunTimeoutSeconds", "60" }, { "claudeRunTimeoutSeconds", "3600" },
        { "claudeMaxItemsPerRun", "1" }, { "claudeMaxItemsPerRun", "50" },
        { "claudeMaxTurns", "10" }, { "claudeMaxTurns", "300" },
    };

    [Theory]
    [MemberData(nameof(EdgeValues))]
    public async Task Edge_values_are_accepted(string field, string value)
    {
        var response = await PutJsonAsync($$"""{"{{field}}":{{value}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct)).ShouldNotBeNull()[field].ShouldNotBeNull();
        saved.GetValue<double>().ShouldBe(double.Parse(value, CultureInfo.InvariantCulture));
    }

    public static TheoryData<string, string> OutOfRangeValues => new()
    {
        { "claudeSuggestThreshold", "0.29" }, { "claudeSuggestThreshold", "0.96" },
        { "claudeRunTimeoutSeconds", "59" }, { "claudeRunTimeoutSeconds", "3601" },
        { "claudeMaxItemsPerRun", "0" }, { "claudeMaxItemsPerRun", "51" },
        { "claudeMaxTurns", "9" }, { "claudeMaxTurns", "301" },
        { "claudeModel", $"\"{new string('m', SettingsValidation.MaxModelNameLength + 1)}\"" },
        { "claudeModel", "\"model\\nname\"" },
    };

    [Theory]
    [MemberData(nameof(OutOfRangeValues))]
    public async Task Out_of_range_values_get_400_field_errors(string field, string value)
    {
        var response = await PutJsonAsync($$"""{"{{field}}":{{value}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull();
        problem.Errors.Keys.ShouldBe([field]);
        await using var db = postgres.CreateDbContext();
        (await db.Settings.AnyAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Token_set_in_env_is_reported_but_never_returned_or_stored()
    {
        await using var withToken = factory.WithWebHostBuilder(b => b.UseSetting(SettingsEnvOptions.ClaudeCodeOAuthTokenKey, SyntheticToken));

        var response = await PutAsync(withToken, new UpdateSettingsRequest(null, null, null, null, ClaudeReviewerMode: ClaudeReviewerMode.HeadlessClaudeCode));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldContain("\"claudeTokenSet\":true");
        body.ShouldNotContain(SyntheticToken);
        (await withToken.CreateClient().GetStringAsync("/api/settings", Ct)).ShouldNotContain(SyntheticToken);
        await using var db = postgres.CreateDbContext();
        var document = (await db.Settings.SingleAsync(Ct)).Document;
        document.ShouldNotContain(SyntheticToken);
        document.ShouldNotContain("claudeTokenSet", Case.Insensitive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Headless_mode_saves_without_a_token_and_reports_it_unset(string token)
    {
        await using var noToken = factory.WithWebHostBuilder(b => b.UseSetting(SettingsEnvOptions.ClaudeCodeOAuthTokenKey, token));

        var response = await PutAsync(noToken, new UpdateSettingsRequest(null, null, null, null, ClaudeReviewerMode: ClaudeReviewerMode.HeadlessClaudeCode));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = (await response.Content.ReadFromJsonAsync<SettingsDto>(Ct)).ShouldNotBeNull();
        saved.ClaudeReviewerMode.ShouldBe(ClaudeReviewerMode.HeadlessClaudeCode);
        saved.ClaudeTokenSet.ShouldBeFalse();
    }

    private Task<HttpResponseMessage> PutJsonAsync(string json) => PutAsync(factory, JsonNode.Parse(json));

    private static async Task<SettingsDto> GetAsync(WebApplicationFactory<Program> host) =>
        (await host.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();

    private static async Task<HttpResponseMessage> PutAsync<T>(WebApplicationFactory<Program> host, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = JsonContent.Create(body) };
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return await host.CreateClient().SendAsync(request, Ct);
    }
}
