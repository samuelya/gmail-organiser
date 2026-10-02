using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Common;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The <c>attachments</c> block of the settings endpoints and the policy that reads it (#70).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AttachmentSettingsEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Defaults_when_the_document_lacks_the_block()
    {
        await SeedDocumentAsync("""{"chatModel":"chat-model-a"}""");

        var attachments = (await GetAsync()).Attachments;

        attachments.ShouldBe(new AttachmentSettings { Types = attachments.Types });
        attachments.Types.ShouldBe(AttachmentSettings.DefaultTypes);
    }

    [Fact]
    public async Task Block_round_trips_with_snake_case_types_and_is_kept_by_other_updates()
    {
        var response = await PutJsonAsync("""
            {"attachments":{"enabled":false,"types":[{"type":"image","enabled":false},{"type":"other","enabled":true}],
             "maxBytes":65536,"maxImageBytes":26214400,"maxChars":50000,"maxPerMessage":1}}
            """);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("""{"type":"word_document","enabled":true}""");
        await PutJsonAsync("""{"attachments":{"maxChars":1234}}""");
        await PutJsonAsync("""{"chatModel":"chat-model-a"}""");

        var attachments = (await GetAsync()).Attachments;

        attachments.Enabled.ShouldBeFalse();
        attachments.MaxBytes.ShouldBe(65536);
        attachments.MaxImageBytes.ShouldBe(25 * 1024 * 1024);
        attachments.MaxChars.ShouldBe(1234);
        attachments.MaxPerMessage.ShouldBe(1);
        attachments.Types.ShouldBe(AttachmentSettings.WithDefaults(
            [new(AttachmentType.Image, false), new(AttachmentType.Other, true)]));
    }

    [Fact]
    public async Task Updates_store_only_the_changed_values_so_the_rest_follow_the_defaults()
    {
        await PutJsonAsync("""{"attachments":{"enabled":false}}""");
        await PutJsonAsync("""{"attachments":{"types":[{"type":"pdf","enabled":false}]}}""");
        await PutJsonAsync("""{"attachments":{"types":[{"type":"image","enabled":false}]}}""");
        await PutJsonAsync("""{"attachments":{"types":[{"type":"pdf","enabled":false}]}}""");

        await using var db = postgres.CreateDbContext();
        var document = JsonNode.Parse((await db.Settings.SingleAsync(Ct)).Document)!.AsObject();
        JsonNode.DeepEquals(document, JsonNode.Parse("""
            {"attachments":{"enabled":false,"types":[{"type":"pdf","enabled":false},{"type":"image","enabled":false}]}}
            """)).ShouldBeTrue(document.ToJsonString());
        var attachments = (await GetAsync()).Attachments;
        attachments.MaxChars.ShouldBe(AttachmentSettings.DefaultMaxChars);
        attachments.Types.ShouldBe(AttachmentSettings.WithDefaults(
            [new(AttachmentType.Pdf, false), new(AttachmentType.Image, false)]));
    }

    [Theory]
    [InlineData("""{"maxBytes":65535}""", "attachments.maxBytes")]
    [InlineData("""{"maxImageBytes":26214401}""", "attachments.maxImageBytes")]
    [InlineData("""{"maxChars":499}""", "attachments.maxChars")]
    [InlineData("""{"maxPerMessage":21}""", "attachments.maxPerMessage")]
    [InlineData("""{"types":[{"type":"hologram","enabled":true}]}""", "attachments.types[0].type")]
    public async Task Invalid_values_get_400_field_errors_and_change_nothing(string block, string field)
    {
        var response = await PutJsonAsync($$"""{"chatModel":"chat-model-a","attachments":{{block}}}""");

        var problem = await ShouldBeValidationProblemAsync(response);
        problem.Errors.Keys.ShouldBe([field]);
        await using var db = postgres.CreateDbContext();
        (await db.Settings.AnyAsync(Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Enabling_archives_gets_400_with_a_clear_detail()
    {
        var response = await PutJsonAsync("""{"attachments":{"types":[{"type":"archive","enabled":true}]}}""");

        var problem = await ShouldBeValidationProblemAsync(response);
        problem.Errors["attachments.types[0].enabled"].Single().ShouldContain("never read");
        (await GetAsync()).Attachments.EnabledTypes().ShouldNotContain(AttachmentType.Archive);
    }

    [Fact]
    public async Task Saved_partial_block_merges_with_defaults_and_drives_the_policy()
    {
        await SeedDocumentAsync("""
            {"attachments":{"types":[{"type":"pdf","enabled":false},{"type":"hologram","enabled":true},{"type":"archive","enabled":true}],
             "maxChars":"many","maxPerMessage":7,"unknown":1}}
            """);

        var attachments = (await GetAsync()).Attachments;
        await using var scope = factory.Services.CreateAsyncScope();
        var policy = await scope.ServiceProvider.GetRequiredService<IAttachmentPolicy>().GetAsync(Ct);

        attachments.Enabled.ShouldBeTrue();
        attachments.MaxChars.ShouldBe(AttachmentSettings.DefaultMaxChars);
        attachments.MaxPerMessage.ShouldBe(7);
        attachments.Types.Single(t => t.Type == AttachmentType.Pdf).Enabled.ShouldBeFalse();
        attachments.Types.Single(t => t.Type == AttachmentType.Archive).Enabled.ShouldBeFalse();
        attachments.Types.Single(t => t.Type == AttachmentType.Image).Enabled.ShouldBeTrue();
        policy.EnabledTypes.ShouldBe(attachments.EnabledTypes(), ignoreOrder: true);
        policy.Limits.ShouldBe(new ConversionLimits(
            AttachmentSettings.DefaultMaxBytes, AttachmentSettings.DefaultMaxImageBytes, AttachmentSettings.DefaultMaxChars, 7));
    }

    private static async Task<HttpValidationProblemDetails> ShouldBeValidationProblemAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        return (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(Ct)).ShouldNotBeNull();
    }

    private async Task SeedDocumentAsync(string document)
    {
        await using var db = postgres.CreateDbContext();
        db.Settings.Add(new SettingsRow { Document = document });
        await db.SaveChangesAsync(Ct);
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
