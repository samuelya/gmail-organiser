using GmailOrganiser.Data;
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

    public ValueTask DisposeAsync() => container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
