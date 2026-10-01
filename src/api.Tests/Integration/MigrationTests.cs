using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_apply_on_an_empty_database()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await using var db = CreateDbContext(connectionString);

        (await db.Database.GetAppliedMigrationsAsync(Ct)).ShouldBeEmpty();

        await db.Database.MigrateAsync(Ct);

        (await db.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(db.Database.GetMigrations());
        (await db.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Re_running_migrations_is_a_no_op()
    {
        await using var db = postgres.CreateDbContext();
        var before = (await db.Database.GetAppliedMigrationsAsync(Ct)).ToList();

        await db.Database.MigrateAsync(Ct);

        (await db.Database.GetAppliedMigrationsAsync(Ct)).ShouldBe(before);
        before.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Vector_extension_exists()
    {
        await using var db = postgres.CreateDbContext();

        var count = await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_extension WHERE extname = 'vector'")
            .SingleAsync(Ct);

        count.ShouldBe(1);
    }

    [Fact]
    public void Model_has_no_pending_changes()
    {
        using var db = postgres.CreateDbContext();

        db.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"empty_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(postgres.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;
    }

    private static AppDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, connectionString);
        return new AppDbContext(options.Options);
    }
}
