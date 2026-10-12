using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Dashboard;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Senders;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// The Dashboard's mailbox mix (#470). Exact counts run on a database of their own in the shared container, since other
/// classes leave senders and suggestions in the shared one; the endpoint test only checks the wire shape.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MailboxBreakdownTests(ApiFactory factory, BreakdownDatabase database)
    : IClassFixture<ApiFactory>, IClassFixture<BreakdownDatabase>
{
    private static readonly DateTimeOffset Start = new(2100, 4, 1, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Empty_database_returns_zeros_with_every_mail_type_in_enum_order()
    {
        await using var db = await database.CreateEmptyAsync(Ct);

        var breakdown = await MailboxBreakdownQuery.LoadAsync(db, Ct);

        breakdown.Categories.ShouldBe(new SenderCategoryMixDto(0, 0, 0, 0, 0));
        breakdown.MailTypes.Select(t => t.MailType).ShouldBe(AllMailTypeNames);
        breakdown.MailTypes.ShouldAllBe(t => t.Count == 0);
        breakdown.Unclassified.ShouldBe(0);
    }

    [Fact]
    public async Task Sums_sender_categories_and_counts_non_rejected_suggestions_per_mail_type()
    {
        await using var db = await database.CreateEmptyAsync(Ct);
        db.Senders.AddRange(
            Sender("a@one-470.example.com", primary: 3, promotions: 10, social: 1, updates: 4, forums: 0),
            Sender("b@two-470.example.com", primary: 2, promotions: 5, social: 0, updates: 6, forums: 7));
        Suggest(db, 1, MailType.Receipt, SuggestionStatus.Pending);
        Suggest(db, 2, MailType.Receipt, SuggestionStatus.Applied);
        Suggest(db, 3, MailType.Receipt, SuggestionStatus.Rejected);
        Suggest(db, 4, MailType.Newsletter, SuggestionStatus.Approved);
        Suggest(db, 5, MailType.SecurityOtp, SuggestionStatus.Rejected);
        Suggest(db, 6, null, SuggestionStatus.Pending);
        Suggest(db, 7, null, SuggestionStatus.Applied);
        Suggest(db, 8, null, SuggestionStatus.Rejected);
        await db.SaveChangesAsync(Ct);

        var breakdown = await MailboxBreakdownQuery.LoadAsync(db, Ct);

        breakdown.Categories.ShouldBe(new SenderCategoryMixDto(5, 15, 1, 10, 7));
        breakdown.MailTypes.Select(t => t.MailType).ShouldBe(AllMailTypeNames);
        breakdown.MailTypes.ToDictionary(t => t.MailType, t => t.Count).ShouldBe(
            Enum.GetValues<MailType>().ToDictionary(SnakeCaseEnumConverter<MailType>.ToDb, t => t switch
            {
                MailType.Receipt => 2,
                MailType.Newsletter => 1,
                _ => 0,
            }));
        breakdown.Unclassified.ShouldBe(2);
    }

    [Fact]
    public async Task Endpoint_writes_mail_types_as_snake_case_names()
    {
        using var client = factory.CreateClient();

        using var json = JsonDocument.Parse(
            await client.GetStringAsync(new Uri("/api/dashboard/breakdown", UriKind.Relative), Ct));

        var root = json.RootElement;
        root.GetProperty("categories").GetProperty("promotions").ValueKind.ShouldBe(JsonValueKind.Number);
        root.GetProperty("unclassified").ValueKind.ShouldBe(JsonValueKind.Number);
        root.GetProperty("mailTypes").EnumerateArray().Select(t => t.GetProperty("mailType").GetString())
            .ShouldBe(AllMailTypeNames);
    }

    private static IEnumerable<string> AllMailTypeNames => Enum.GetValues<MailType>().Select(SnakeCaseEnumConverter<MailType>.ToDb);

    private static SenderRow Sender(string address, int primary, int promotions, int social, int updates, int forums) => new()
    {
        Address = address,
        Domain = address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..],
        PrimaryCount = primary,
        PromotionsCount = promotions,
        SocialCount = social,
        UpdatesCount = updates,
        ForumsCount = forums,
        UpdatedAt = Start,
    };

    private static void Suggest(AppDbContext db, int n, MailType? mailType, SuggestionStatus status)
    {
        const string from = "c@three-470.example.com";
        var message = new MessageRow
        {
            Id = $"breakdown-470-{n}",
            ThreadId = $"breakdown-470-{n}",
            FromAddress = from,
            CanonicalAddress = from,
            CanonicalDomain = "three-470.example.com",
            InternalDate = Start,
            FetchedAt = Start,
            UpdatedAt = Start,
        };
        var suggestion = new SuggestionRow
        {
            Id = Guid.NewGuid(),
            MessageId = message.Id,
            SenderAddress = from,
            Source = SuggestionSource.Llm,
            TopicLabel = "Example/Topic",
            MailType = mailType,
            Confidence = 0.9,
            Reason = "Synthetic reason",
            CreatedAt = Start,
        };
        suggestion.SetStatus(status, message, Start);
        db.Messages.Add(message);
        db.Suggestions.Add(suggestion);
    }
}

/// <summary>One migrated database per test class in the shared container; each test starts from <see cref="CreateEmptyAsync"/>.</summary>
public sealed class BreakdownDatabase(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly string connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
    {
        Database = $"breakdown_{Guid.NewGuid():N}",
    }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await using var db = Create();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = Create();
        await db.Database.EnsureDeletedAsync(CancellationToken.None);
    }

    /// <summary>A context on the class database with the tables these tests write cleared.</summary>
    public async Task<AppDbContext> CreateEmptyAsync(CancellationToken ct)
    {
        var db = Create();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE suggestions, messages, senders CASCADE", ct);
        return db;
    }

    private AppDbContext Create()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, connectionString);
        return new AppDbContext(options.Options);
    }
}
