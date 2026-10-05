using System.Net;
using System.Net.Http.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class SenderAllowlistTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Known = "known@example.com";
    private static readonly DateTimeOffset Seen = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Jobs.ExecuteDeleteAsync();
        await db.Senders.ExecuteDeleteAsync();
        await db.Settings.ExecuteDeleteAsync();
        db.Senders.AddRange(
            new SenderRow { Address = Known, Domain = "example.com", DisplayName = "Known", TotalCount = 7, AnalysedCount = 3, AppliedCount = 1, LastSeenAt = Seen, UpdatedAt = Seen },
            new SenderRow { Address = "other@example.org", Domain = "example.org", TotalCount = 2, LastSeenAt = Seen, UpdatedAt = Seen });
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Allowlisting_a_known_sender_sets_the_flag_and_keeps_its_counts()
    {
        var dto = await PutOkAsync(Known, true);

        (dto.Address, dto.DisplayName, dto.TotalCount, dto.AnalysedCount, dto.AppliedCount, dto.LastSeenAt, dto.Allowlisted)
            .ShouldBe((Known, "Known", 7, 3, 1, Seen, true));
        (await PutOkAsync(Known, true)).Allowlisted.ShouldBeTrue();
        (await PutOkAsync(Known, false)).Allowlisted.ShouldBeFalse();
        await using var db = postgres.CreateDbContext();
        (await db.Senders.SingleAsync(s => s.Address == Known, Ct)).TotalCount.ShouldBe(7);
    }

    [Fact]
    public async Task Allowlisting_an_unknown_address_creates_a_stub_row_normalised_like_fetch()
    {
        var dto = await PutOkAsync(Uri.EscapeDataString("  New.Sender@Sub.Example.COM "), true);

        dto.ShouldBe(new SenderDto("new.sender@sub.example.com", "sub.example.com", null, 0, 0, 0, null, true, false, null, null,
            "new.sender@sub.example.com", "sub.example.com", false, SenderKind.Unknown, 0, 0, null, 0, new SenderCategoryMixDto(0, 0, 0, 0, 0)));
        await using var db = postgres.CreateDbContext();
        (await db.Senders.CountAsync(s => s.Address == "new.sender@sub.example.com", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Removing_an_unknown_address_is_a_404_and_creates_nothing()
    {
        var response = await PutAsync("nobody@example.com", new AllowlistRequest(false));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct)).ShouldNotBeNull().Status.ShouldBe(404);
        await using var db = postgres.CreateDbContext();
        (await db.Senders.AnyAsync(s => s.Address == "nobody@example.com", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Concurrent_allowlisting_of_one_new_address_all_succeed_with_one_row()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PutAsync("race@example.com", new AllowlistRequest(true))));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        await using var db = postgres.CreateDbContext();
        (await db.Senders.CountAsync(s => s.Address == "race@example.com" && s.Allowlisted, Ct)).ShouldBe(1);
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("two@at@example.com")]
    [InlineData("@example.com")]
    [InlineData("local@")]
    [InlineData("with%20space@example.com")]
    [InlineData("tab%09@example.com")]
    [InlineData("%22quoted%22@example.com")]
    [InlineData("a@example.com,b@example.com")]
    [InlineData("a@example.com;b@example.com")]
    [InlineData("group:a@example.com")]
    [InlineData("Shop%20%3Cshop@example.com%3E")]
    [InlineData("%3C%3Cshop@example.com%3E%3E")]
    [InlineData("%3Cshop@example.com")]
    [InlineData("%3C%3E")]
    public async Task An_invalid_address_is_a_400_on_address(string address)
    {
        var response = await PutAsync(address, new AllowlistRequest(true));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors.Keys.ShouldBe(["address"]);
    }

    [Fact]
    public async Task An_address_in_angle_brackets_is_stored_without_them_like_fetch()
    {
        var dto = await PutOkAsync(Uri.EscapeDataString(" < Shop@Example.com > "), true);

        dto.Address.ShouldBe("shop@example.com");
        await using var db = postgres.CreateDbContext();
        (await db.Senders.CountAsync(s => s.Address.Contains("<"), Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task An_address_over_320_characters_is_a_400_and_320_is_accepted()
    {
        var domain = "@example.com";
        var longest = new string('a', SenderAllowlist.MaxAddressLength - domain.Length) + domain;

        (await PutAsync("a" + longest, new AllowlistRequest(true))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PutOkAsync(longest, true)).Address.ShouldBe(longest);
    }

    [Fact]
    public async Task A_missing_flag_is_a_400_and_changes_nothing()
    {
        await PutOkAsync(Known, true);

        var response = await PutAsync(Known, new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors.Keys.ShouldBe(["allowlisted"]);
        await using var db = postgres.CreateDbContext();
        (await db.Senders.SingleAsync(s => s.Address == Known, Ct)).Allowlisted.ShouldBeTrue();
    }

    [Fact]
    public async Task The_list_filters_on_the_flag_including_stub_rows()
    {
        await PutOkAsync(Known, true);
        await PutOkAsync("stub@example.net", true);

        (await ListAsync("?allowlisted=true")).Items.Select(s => s.Address).ShouldBe([Known, "stub@example.net"], ignoreOrder: true);
        (await ListAsync("?allowlisted=false")).Items.Select(s => s.Address).ShouldBe(["other@example.org"]);
        (await ListAsync("")).Total.ShouldBe(3);
        (await ListAsync("?allowlisted=true&search=stub")).Items.Single().TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_stats_update_keeps_a_zero_count_allowlisted_stub()
    {
        await PutOkAsync("stub@example.net", true);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SenderStatsUpdater>().UpdateAsync(["stub@example.net", "fresh@example.net"], Ct);
        }

        await using var db = postgres.CreateDbContext();
        var stub = await db.Senders.SingleAsync(s => s.Address == "stub@example.net", Ct);
        (stub.Allowlisted, stub.TotalCount, stub.LastSeenAt).ShouldBe((true, 0, (DateTimeOffset?)null));
        var fresh = await db.Senders.SingleAsync(s => s.Address == "fresh@example.net", Ct);
        (fresh.Domain, fresh.Allowlisted, fresh.TotalCount).ShouldBe(("example.net", false, 0));
    }

    [Fact]
    public async Task A_listed_domain_marks_its_senders_and_subdomains_as_allowlisted_by_domain()
    {
        var put = await PutJsonAsync("/api/settings", new UpdateSettingsRequest(
            null, null, null, null, Protection: new UpdateProtectionSettingsRequest(AllowlistedDomains: [" Example.COM ", "example.com"])));
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var settings = (await factory.CreateClient().GetFromJsonAsync<SettingsDto>("/api/settings", Ct)).ShouldNotBeNull();
        settings.Protection.AllowlistedDomains.ShouldBe(["example.com"]);

        var list = await ListAsync("?sort=address&dir=asc");
        list.Items.Select(s => (s.Address, s.Allowlisted, s.AllowlistedByDomain))
            .ShouldBe([(Known, false, true), ("other@example.org", false, false)]);
        (await ListAsync("?allowlisted=true")).Total.ShouldBe(0);
        (await PutOkAsync(Uri.EscapeDataString("new@mail.example.com"), true)).AllowlistedByDomain.ShouldBeTrue();

        var bad = await PutJsonAsync("/api/settings", new UpdateSettingsRequest(
            null, null, null, null, Protection: new UpdateProtectionSettingsRequest(AllowlistedDomains: ["user@example.com"])));
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = (await bad.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct)).ShouldNotBeNull();
        problem.Errors.Keys.ShouldBe([SettingsValidation.AllowlistedDomainsField]);
    }

    [Fact]
    public async Task Without_the_csrf_header_the_put_is_refused()
    {
        var response = await factory.CreateClient().PutAsJsonAsync($"/api/senders/{Known}/allowlist", new AllowlistRequest(true), Ct);

        response.IsSuccessStatusCode.ShouldBeFalse();
        await using var db = postgres.CreateDbContext();
        (await db.Senders.SingleAsync(s => s.Address == Known, Ct)).Allowlisted.ShouldBeFalse();
    }

    private async Task<SenderDto> PutOkAsync(string address, bool allowlisted)
    {
        var response = await PutAsync(address, new AllowlistRequest(allowlisted));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SenderDto>(Ct)).ShouldNotBeNull();
    }

    private Task<HttpResponseMessage> PutAsync(string address, object body) => PutJsonAsync($"/api/senders/{address}/allowlist", body);

    private Task<HttpResponseMessage> PutJsonAsync(string path, object body)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client.PutAsJsonAsync(path, body, Ct);
    }

    private async Task<PagedDto<SenderDto>> ListAsync(string query) =>
        (await factory.CreateClient().GetFromJsonAsync<PagedDto<SenderDto>>($"/api/senders{query}", Ct)).ShouldNotBeNull();
}
