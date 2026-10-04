using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// One throwaway <c>pgvector/pgvector:pg17</c> container per test collection, with the migrations applied.
/// Use it through <c>[Collection(PostgresCollection.Name)]</c>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "pgvector/pgvector:pg17";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(Image).Build();

    public string ConnectionString => container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, ConnectionString);
        return new AppDbContext(options.Options);
    }

    /// <summary>
    /// Puts the singleton <c>fetch_state</c> row back to its migration seed, every column. Classes that write the row
    /// call it on set-up and tear-down, so no class sees another's fetch state.
    /// </summary>
    public async Task ResetFetchStateAsync()
    {
        await using var db = CreateDbContext();
        var seed = new FetchStateRow { UpdatedAt = DateTimeOffset.UnixEpoch };
        await db.FetchState.ExecuteUpdateAsync(s => s
            .SetProperty(r => r.AccountEmail, seed.AccountEmail)
            .SetProperty(r => r.MailboxPhase, seed.MailboxPhase)
            .SetProperty(r => r.PageToken, seed.PageToken)
            .SetProperty(r => r.InboxFetched, seed.InboxFetched)
            .SetProperty(r => r.AllMailFetched, seed.AllMailFetched)
            .SetProperty(r => r.MessagesTotal, seed.MessagesTotal)
            .SetProperty(r => r.InboxTotal, seed.InboxTotal)
            .SetProperty(r => r.AllMailTotal, seed.AllMailTotal)
            .SetProperty(r => r.LastHistoryId, seed.LastHistoryId)
            .SetProperty(r => r.StartedAt, seed.StartedAt)
            .SetProperty(r => r.CompletedAt, seed.CompletedAt)
            .SetProperty(r => r.UpdatedAt, seed.UpdatedAt));
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
