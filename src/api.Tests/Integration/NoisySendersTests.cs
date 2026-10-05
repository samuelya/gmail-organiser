using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Senders;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class NoisySendersTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Senders.ExecuteDeleteAsync(Ct);
        db.Senders.AddRange(
            Row("bulk@news.example.com", total: 30, unread: 29, kind: SenderKind.Bulk, lastSeenDaysAgo: 10),
            // Two relay addresses behind one canonical sender: listed once, with the sums.
            Row("r1@relay.example.net", "promo@shop.example.com", total: 8, unread: 8, kind: SenderKind.Bulk),
            Row("r2@relay.example.net", "promo@shop.example.com", total: 7, unread: 7, kind: SenderKind.Mixed),
            Row("old@old.example.com", total: 12, unread: 12, kind: SenderKind.Bulk, lastSeenDaysAgo: 400),
            Row("replied@chat.example.com", total: 40, unread: 40, replied: 1),
            // One replied-to raw address excludes its whole canonical group.
            Row("h1@relay.example.net", "hidden@quiet.example.com", total: 20, unread: 20),
            Row("h2@relay.example.net", "hidden@quiet.example.com", total: 2, unread: 0, replied: 1),
            Row("kept@list.example.com", total: 50, unread: 50, allowlisted: true),
            Row("deals@safe.example.org", total: 50, unread: 50),
            Row("person@people.example.com", total: 25, unread: 25, kind: SenderKind.Human),
            Row("half@mixed.example.com", total: 20, unread: 10),
            Row("few@small.example.com", total: 5, unread: 5),
            Row("nostats@new.example.com", total: 30, unread: 0, stats: false));
        await db.SaveChangesAsync(Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
            .UpdateAsync(s => s with { Protection = s.Protection with { AllowlistedDomains = ["example.org"] } }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
            .UpdateAsync(s => s with { Protection = s.Protection with { AllowlistedDomains = [] } }, Ct);
    }

    private static SenderRow Row(
        string address, string? canonical = null, int total = 0, int unread = 0, int replied = 0,
        SenderKind kind = SenderKind.Unknown, int lastSeenDaysAgo = 1, bool allowlisted = false, bool stats = true)
    {
        canonical ??= address;
        return new SenderRow
        {
            Address = address,
            Domain = address[(address.IndexOf('@') + 1)..],
            CanonicalAddress = canonical,
            CanonicalDomain = canonical[(canonical.IndexOf('@') + 1)..],
            IsRelay = canonical != address,
            TotalCount = total,
            UnreadCount = unread,
            RepliedCount = replied,
            ListUnsubscribeCount = total,
            PromotionsCount = total,
            Kind = kind,
            FirstSeenAt = Now.AddDays(-lastSeenDaysAgo - 30),
            LastSeenAt = Now.AddDays(-lastSeenDaysAgo),
            StatsAt = stats ? Now : null,
            Allowlisted = allowlisted,
            UpdatedAt = Now,
        };
    }

    [Fact]
    public async Task Lists_bulk_unread_senders_by_canonical_address_and_volume()
    {
        var page = await GetAsync("/api/senders/noisy");

        page.Total.ShouldBe(3);
        page.PageSize.ShouldBe(50);
        page.Items.Select(s => s.CanonicalAddress).ShouldBe(["bulk@news.example.com", "promo@shop.example.com", "old@old.example.com"]);

        var relay = page.Items[1];
        relay.CanonicalDomain.ShouldBe("shop.example.com");
        relay.Addresses.ShouldBe(["r1@relay.example.net", "r2@relay.example.net"]);
        relay.TotalCount.ShouldBe(15);
        relay.UnreadCount.ShouldBe(15);
        relay.UnreadRatio.ShouldBe(1);
        relay.ListUnsubscribeCount.ShouldBe(15);
        relay.CategoryMix.ShouldBe(new SenderCategoryMix(0, 15, 0, 0, 0));
        relay.Kind.ShouldBe(SenderKind.Mixed);
        relay.FirstSeenAt.ShouldBe(Now.AddDays(-31));
        relay.LastSeenAt.ShouldBe(Now.AddDays(-1));
        relay.HasApprovedPolicy.ShouldBeFalse();
    }

    [Fact]
    public async Task Thresholds_and_search_narrow_the_list()
    {
        (await GetAsync("/api/senders/noisy?minMessages=5&minUnreadRatio=0.5")).Items.Select(s => s.CanonicalAddress)
            .ShouldBe(["bulk@news.example.com", "half@mixed.example.com", "promo@shop.example.com", "old@old.example.com", "few@small.example.com"]);
        (await GetAsync("/api/senders/noisy?minMessages=13")).Total.ShouldBe(2);
        // A search on a raw relay address finds its canonical sender, with the whole group's sums.
        var found = await GetAsync("/api/senders/noisy?search=r2%40relay");
        found.Items.ShouldHaveSingleItem().TotalCount.ShouldBe(15);
    }

    [Fact]
    public async Task Kind_serialises_as_its_snake_case_name()
    {
        var json = await factory.CreateClient().GetStringAsync("/api/senders/noisy?pageSize=1", Ct);

        JsonDocument.Parse(json).RootElement.GetProperty("items")[0].GetProperty("kind").GetString().ShouldBe("bulk");
    }

    [Fact]
    public async Task Dormant_filter_keeps_senders_last_seen_before_the_cutoff()
    {
        var query = NoisySenderQuery.Parse(null, null, 365, null, null, null, out var errors).ShouldNotBeNull();
        errors.ShouldBeEmpty();
        await using var db = postgres.CreateDbContext();

        var page = await query.ExecuteAsync(db, ["example.org"], new FakeTimeProvider(Now), Ct);
        page.Items.ShouldHaveSingleItem().CanonicalAddress.ShouldBe("old@old.example.com");

        var later = await query.ExecuteAsync(db, ["example.org"], new FakeTimeProvider(Now.AddDays(360)), Ct);
        later.Items.Select(s => s.CanonicalAddress).ShouldBe(["bulk@news.example.com", "old@old.example.com"]);
    }

    [Theory]
    [InlineData("minMessages=0")]
    [InlineData("minMessages=100001")]
    [InlineData("minUnreadRatio=-0.1")]
    [InlineData("minUnreadRatio=1.5")]
    [InlineData("minUnreadRatio=NaN")]
    [InlineData("dormantDays=0")]
    [InlineData("dormantDays=3651")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=201")]
    [InlineData("page=0")]
    public async Task Invalid_query_is_a_400_validation_problem(string query)
    {
        var response = await factory.CreateClient().GetAsync($"/api/senders/noisy?{query}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        problem.ShouldNotBeNull().Errors.Keys.ShouldHaveSingleItem().ShouldBe(query[..query.IndexOf('=')]);
    }

    private async Task<PagedDto<NoisySenderDto>> GetAsync(string url) =>
        (await factory.CreateClient().GetFromJsonAsync<PagedDto<NoisySenderDto>>(url, Ct)).ShouldNotBeNull();
}
