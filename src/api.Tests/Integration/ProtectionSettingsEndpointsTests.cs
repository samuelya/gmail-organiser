using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using GmailOrganiser.Common;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The <c>protection</c> block of the settings endpoints (#176).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProtectionSettingsEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_rule_is_on_when_the_document_lacks_the_block()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Settings.Add(new SettingsRow { Document = """{"chatModel":"chat-model-a"}""" });
            await db.SaveChangesAsync(Ct);
        }

        (await GetAsync()).Protection.ShouldBe(new ProtectionSettings(true, true, true, true));
    }

    [Fact]
    public async Task Block_round_trips_as_a_partial_update_and_is_kept_by_other_updates()
    {
        var response = await PutJsonAsync("""{"protection":{"starred":false,"repliedThreads":false}}""");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SettingsDto>(Ct)).ShouldNotBeNull().Protection
            .ShouldBe(new ProtectionSettings(Attachments: true, Starred: false, Important: true, RepliedThreads: false));

        await PutJsonAsync("""{"protection":{"attachments":false,"starred":null}}""");
        await PutJsonAsync("""{"chatModel":"chat-model-a"}""");

        (await GetAsync()).Protection.ShouldBe(new ProtectionSettings(Attachments: false, Starred: false, Important: true, RepliedThreads: false));
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
