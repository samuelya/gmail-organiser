using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GmailOrganiser.Common;
using GmailOrganiser.Fetch;
using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class SendersEndpointsTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const int SenderCount = 300;
    private static readonly DateTimeOffset Newest = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 300 synthetic senders over three domains. Total counts repeat every 10 senders so the address tiebreaker
    /// decides the order inside a group; every 50th sender has never been seen.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Jobs.ExecuteDeleteAsync();
        await db.Senders.ExecuteDeleteAsync();
        db.Senders.AddRange(Enumerable.Range(0, SenderCount).Select(i => new SenderRow
        {
            Address = $"s{i:D3}@{Domain(i)}",
            Domain = Domain(i),
            DisplayName = i % 3 == 0 ? $"Person {i:D3}" : null,
            TotalCount = i % 10,
            AnalysedCount = i % 7,
            LastSeenAt = i % 50 == 0 ? null : Newest.AddMinutes(-i),
            UpdatedAt = Newest,
        }));
        db.Senders.AddRange(
            new SenderRow { Address = "under_score@wild.example.com", Domain = "wild.example.com", UpdatedAt = Newest },
            new SenderRow { Address = "percent@wild.example.com", Domain = "wild.example.com", DisplayName = "100% Off", UpdatedAt = Newest });
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Domain(int i) => $"d{i % 3}.example.com";

    [Fact]
    public async Task Default_is_total_desc_page_1_of_50_with_the_address_tiebreaker()
    {
        var page = await GetAsync("/api/senders");

        page.Page.ShouldBe(1);
        page.PageSize.ShouldBe(SenderQuery.DefaultPageSize);
        page.Total.ShouldBe(SenderCount + 2);
        page.Items.Count.ShouldBe(50);
        page.Items.ShouldAllBe(s => s.TotalCount == 9 || s.TotalCount == 8);
        page.Items.Take(30).Select(s => s.Address).ShouldBe(
            Enumerable.Range(0, SenderCount).Where(i => i % 10 == 9).Select(i => $"s{i:D3}@{Domain(i)}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Paging_through_every_page_returns_each_sender_once_in_a_stable_order()
    {
        var seen = new List<string>();
        for (var p = 1; p <= 4; p++)
        {
            var page = await GetAsync($"/api/senders?page={p}&pageSize=100&sort=analysed&dir=asc");
            page.Total.ShouldBe(SenderCount + 2);
            seen.AddRange(page.Items.Select(s => s.Address));
        }

        seen.Count.ShouldBe(SenderCount + 2);
        seen.Distinct().Count().ShouldBe(SenderCount + 2);
        (await GetAsync("/api/senders?page=5&pageSize=100")).Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("address", "asc")]
    [InlineData("ADDRESS", "DESC")]
    [InlineData("lastSeen", "desc")]
    [InlineData("lastSeen", "asc")]
    public async Task Sorts_by_the_chosen_column(string sort, string dir)
    {
        var page = await GetAsync($"/api/senders?sort={sort}&dir={dir}&pageSize=200&search=example.com");
        var items = page.Items;

        if (sort.Equals("address", StringComparison.OrdinalIgnoreCase))
        {
            var expected = items.Select(s => s.Address).Order(StringComparer.Ordinal);
            items.Select(s => s.Address).ShouldBe(dir == "asc" ? expected : expected.Reverse());
            return;
        }

        // Never-seen senders come last in both directions.
        var seen = items.TakeWhile(s => s.LastSeenAt is not null).Select(s => s.LastSeenAt!.Value).ToList();
        items.Skip(seen.Count).ShouldAllBe(s => s.LastSeenAt == null);
        seen.ShouldBe(dir == "asc" ? seen.Order() : seen.OrderDescending());
    }

    [Theory]
    [InlineData("D1.EXAMPLE", 100)]
    [InlineData("person 00", 4)]
    [InlineData("s12", 10)]
    [InlineData("_", 1)]
    [InlineData("%", 1)]
    [InlineData("nobody", 0)]
    public async Task Search_is_a_case_insensitive_literal_contains_on_address_domain_and_name(string search, int expected)
    {
        var page = await GetAsync($"/api/senders?search={Uri.EscapeDataString(search)}&pageSize=200");

        page.Total.ShouldBe(expected);
        page.Items.Count.ShouldBe(expected);
    }

    [Fact]
    public async Task Search_query_uses_the_trigram_index_on_each_searched_column()
    {
        await using var db = postgres.CreateDbContext();
        await db.Database.OpenConnectionAsync(Ct);
        await using var tx = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off", Ct);
        await using var explain = SenderQuery.Filter(db.Senders.AsNoTracking(), "example").CreateDbCommand();
        explain.Transaction = tx.GetDbTransaction();
        explain.CommandText = "EXPLAIN " + explain.CommandText;

        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        var text = string.Join('\n', plan);
        text.ShouldContain("ix_senders_address_trgm", customMessage: text);
        text.ShouldContain("ix_senders_domain_trgm", customMessage: text);
        text.ShouldContain("ix_senders_display_name_trgm", customMessage: text);
        text.ShouldNotContain("Seq Scan", customMessage: text);
    }

    [Fact]
    public async Task Active_sender_fetch_job_is_attached_to_the_matching_address_or_domain()
    {
        // Start the host first: startup recovery ends any job already marked running.
        _ = factory.CreateClient();
        var job = await AddJobAsync(FetchJobTypes.Sender, JobStatus.Running, new { target = "d2.example.com" });
        await AddJobAsync("other_type", JobStatus.Running, new { target = "s001@d1.example.com" });

        var page = await GetAsync("/api/senders?sort=address&dir=asc&pageSize=6");

        page.Items.Where(s => s.Domain == "d2.example.com").ShouldAllBe(s => s.ActiveFetchJob != null && s.ActiveFetchJob.Id == job);
        page.Items.Where(s => s.Domain != "d2.example.com").ShouldAllBe(s => s.ActiveFetchJob == null);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=201")]
    [InlineData("sort=size")]
    [InlineData("dir=up")]
    [InlineData("kind=human&kind=robot")]
    public async Task Invalid_query_is_a_400_validation_problem(string query)
    {
        var response = await factory.CreateClient().GetAsync($"/api/senders?{query}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        problem.ShouldNotBeNull().Errors.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Kind_filter_is_repeatable_and_returns_the_stage_0_stats()
    {
        await AddStatsSendersAsync();

        var page = await GetAsync("/api/senders?kind=human&kind=BULK&sort=address&dir=asc&pageSize=200");

        page.Items.Select(s => s.Address).ShouldBe(["a@ratio.example.com", "c@ratio.example.com"]);
        var human = page.Items[0];
        human.Kind.ShouldBe(SenderKind.Human);
        human.CanonicalAddress.ShouldBe("a@ratio.example.com");
        human.IsRelay.ShouldBeFalse();
        human.UnreadCount.ShouldBe(1);
        human.RepliedCount.ShouldBe(2);
        human.ListUnsubscribeCount.ShouldBe(3);
        human.FirstSeenAt.ShouldBe(Newest.AddDays(-30));
        human.CategoryMix.ShouldBe(new SenderCategoryMixDto(4, 3, 2, 1, 0));
        page.Items[1].Kind.ShouldBe(SenderKind.Bulk);
        page.Items[1].IsRelay.ShouldBeTrue();
        page.Items[1].CanonicalDomain.ShouldBe("relayed.example.com");

        var raw = await factory.CreateClient().GetStringAsync("/api/senders?kind=mixed", Ct);
        raw.ShouldContain("\"kind\":\"mixed\"");
        (await GetAsync("/api/senders?kind=unknown&search=ratio.example.com")).Items.Select(s => s.Address)
            .ShouldBe(["d@ratio.example.com"]);
    }

    [Theory]
    [InlineData("desc", new[] { "b", "c", "a", "d" })]
    [InlineData("asc", new[] { "a", "c", "b", "d" })]
    public async Task Unread_sort_orders_by_unread_ratio_with_empty_senders_last(string dir, string[] expected)
    {
        await AddStatsSendersAsync();

        var page = await GetAsync($"/api/senders?sort=unread&dir={dir}&search=ratio.example.com");

        page.Items.Select(s => s.Address.Split('@')[0]).ShouldBe(expected);
    }

    /// <summary>Ratios a 1/10, b 9/10, c 5/10, d no messages; kinds human, mixed, bulk (relay), unknown.</summary>
    private async Task AddStatsSendersAsync()
    {
        await using var db = postgres.CreateDbContext();
        db.Senders.AddRange(
            StatsSender("a", 10, 1, SenderKind.Human),
            StatsSender("b", 10, 9, SenderKind.Mixed),
            StatsSender("c", 10, 5, SenderKind.Bulk),
            StatsSender("d", 0, 0, SenderKind.Unknown));
        var relay = db.ChangeTracker.Entries<SenderRow>().Single(e => e.Entity.Address.StartsWith('c')).Entity;
        relay.CanonicalAddress = "news@relayed.example.com";
        relay.CanonicalDomain = "relayed.example.com";
        relay.IsRelay = true;
        await db.SaveChangesAsync(Ct);
    }

    private static SenderRow StatsSender(string local, int total, int unread, SenderKind kind) => new()
    {
        Address = $"{local}@ratio.example.com",
        Domain = "ratio.example.com",
        CanonicalAddress = $"{local}@ratio.example.com",
        CanonicalDomain = "ratio.example.com",
        TotalCount = total,
        UnreadCount = unread,
        RepliedCount = 2,
        ListUnsubscribeCount = 3,
        PrimaryCount = 4,
        PromotionsCount = 3,
        SocialCount = 2,
        UpdatesCount = 1,
        FirstSeenAt = Newest.AddDays(-30),
        Kind = kind,
        UpdatedAt = Newest,
    };

    [Fact]
    public async Task Search_over_200_characters_is_a_400()
    {
        var response = await factory.CreateClient().GetAsync($"/api/senders?search={new string('a', 201)}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct)).ShouldNotBeNull().Errors.Keys.ShouldBe(["search"]);
    }

    [Fact]
    public async Task Search_is_trimmed_before_the_length_check()
    {
        var padded = Uri.EscapeDataString(new string('a', 190) + new string(' ', 20) + "\n");
        var blank = Uri.EscapeDataString(new string(' ', 250));

        (await factory.CreateClient().GetAsync($"/api/senders?search={padded}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = postgres.CreateDbContext();
        (await GetAsync($"/api/senders?search={blank}&pageSize=200")).Total.ShouldBe(await db.Senders.LongCountAsync(Ct));
    }

    private async Task<PagedDto<SenderDto>> GetAsync(string url) =>
        (await factory.CreateClient().GetFromJsonAsync<PagedDto<SenderDto>>(url, Ct)).ShouldNotBeNull();

    private async Task<Guid> AddJobAsync(string type, JobStatus status, object cursor)
    {
        await using var db = postgres.CreateDbContext();
        var row = new JobRow
        {
            Id = Guid.NewGuid(),
            Type = type,
            Queue = JobQueues.Fetch,
            Status = status,
            Cursor = JsonSerializer.Serialize(cursor),
            CreatedAt = Newest,
            UpdatedAt = Newest,
        };
        db.Jobs.Add(row);
        await db.SaveChangesAsync(Ct);
        return row.Id;
    }
}
