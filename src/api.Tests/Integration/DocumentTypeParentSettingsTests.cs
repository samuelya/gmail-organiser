using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The document-type parent setting (#238): default off, round trip, clearing and the clash check.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DocumentTypeParentSettingsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Settings.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Default_is_off()
    {
        (await GetAsync()).DocumentTypeParent.ShouldBeNull();
    }

    [Fact]
    public async Task Parent_is_saved_trimmed_kept_by_other_updates_and_cleared_by_empty()
    {
        (await PutAsync(new UpdateSettingsRequest(null, null, null, null, DocumentTypeParent: "  Synthetic/Docs "))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        await PutAsync(new UpdateSettingsRequest(null, "chat-model-a", null, null));

        (await GetAsync()).DocumentTypeParent.ShouldBe("Synthetic/Docs");

        (await PutAsync(new UpdateSettingsRequest(null, null, null, null, DocumentTypeParent: ""))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GetAsync()).DocumentTypeParent.ShouldBeNull();
    }

    [Fact]
    public async Task Parent_under_the_stored_delete_label_gets_400_and_saves_nothing()
    {
        await PutAsync(new UpdateSettingsRequest(null, null, null, null, DeleteLabelName: "Synthetic Bin"));

        var response = await PutAsync(new UpdateSettingsRequest(null, "chat-model-a", null, null, DocumentTypeParent: "synthetic bin/Docs"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("documentTypeParent");
        var settings = await GetAsync();
        settings.DocumentTypeParent.ShouldBeNull();
        settings.ChatModel.ShouldBeNull();
    }

    private async Task<SettingsDto> GetAsync() =>
        (await factory.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();

    private async Task<HttpResponseMessage> PutAsync(UpdateSettingsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = JsonContent.Create(body) };
        request.Headers.Add(ApiRequestGuardMiddleware.RequestedWithHeader, "XMLHttpRequest");
        return await factory.CreateClient().SendAsync(request, Ct);
    }
}
