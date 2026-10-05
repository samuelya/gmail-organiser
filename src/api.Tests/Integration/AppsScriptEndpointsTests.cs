using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Common;
using GmailOrganiser.Rules;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The <c>appsScript</c> settings block and <c>GET /api/rules/apps-script/config</c> (#210).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AppsScriptEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Block_round_trips_and_is_kept_by_other_updates()
    {
        (await GetAsync()).AppsScript.Rules.ShouldBeEmpty();

        var response = await PutJsonAsync("""
            {"appsScript":{"rules":[{"label":" Synthetic/News ","days":30}],"actionDoneArchive":false,"keepInInboxLabels":["Synthetic/Keep"]}}
            """);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await PutJsonAsync("""{"chatModel":"chat-model-a"}""");

        var saved = (await GetAsync()).AppsScript;
        saved.Rules.ShouldBe([new ArchiveRule("Synthetic/News", 30)]);
        saved.ActionDoneArchive.ShouldBeFalse();
        saved.KeepInInboxLabels.ShouldBe(["Synthetic/Keep"]);
        saved.DryRun.ShouldBeTrue();

        (await PutJsonAsync("""{"appsScript":{"rules":null}}""")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var cleared = (await GetAsync()).AppsScript;
        cleared.Rules.ShouldBeEmpty();
        cleared.KeepInInboxLabels.ShouldBeEmpty();
        cleared.ActionDoneArchive.ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_block_is_a_validation_problem()
    {
        var response = await PutJsonAsync("""{"appsScript":{"rules":[{"label":"INBOX","days":0}]}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct)).ShouldNotBeNull()["errors"]!.AsObject();
        errors.Select(e => e.Key).ShouldBe(["appsScript.rules[0].label", "appsScript.rules[0].days"], ignoreOrder: true);
    }

    [Fact]
    public async Task Config_renders_the_saved_rules_and_action_label()
    {
        await PutJsonAsync("""
            {"actionLabelName":"Synthetic/Act","deleteLabelName":"Synthetic/Bin","appsScript":{"rules":[{"label":"Synthetic/News","days":30}],
             "retentionRules":[{"label":" Synthetic/Receipts ","days":365}],"dryRun":false}}
            """);
        (await GetAsync()).AppsScript.RetentionRules.ShouldBe([new RetentionRule("Synthetic/Receipts", 365)]);

        var dto = (await factory.CreateClient().GetFromJsonAsync<AppsScriptConfigDto>("/api/rules/apps-script/config", Ct)).ShouldNotBeNull();

        dto.ScriptVersion.ShouldBe(2);
        dto.Config.ShouldContain("""{ label: "Synthetic/News", days: 30 },""");
        dto.Config.ShouldContain("""actionLabel: "Synthetic/Act",""");
        dto.Config.ShouldContain("""{ label: "Synthetic/Receipts", days: 365 },""");
        dto.Config.ShouldContain("""toBeDeletedLabel: "Synthetic/Bin",""");
        dto.Config.ShouldContain("dryRun: false,");
        dto.GeneratedAt.ShouldNotBe(default);
    }

    private async Task<SettingsDto> GetAsync() =>
        (await factory.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();

    private async Task<HttpResponseMessage> PutJsonAsync(string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = JsonContent.Create(JsonNode.Parse(json)) };
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return await factory.CreateClient().SendAsync(request, Ct);
    }
}
