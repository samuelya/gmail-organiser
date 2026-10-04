using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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

    [Theory]
    [InlineData("inbox", null, null)]
    [InlineData("all_mail", 7L, null)]
    [InlineData("completed", 7L, 11L)]
    public async Task Totals_backfill_runs_on_a_database_already_at_fetch_totals(string phase, long? inboxTotal, long? allMailTotal)
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await using var db = CreateDbContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync("M2_FetchTotals", cancellationToken: Ct);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE fetch_state SET mailbox_phase = {phase}, inbox_fetched = 7, all_mail_fetched = 11", Ct);

        await db.Database.MigrateAsync(Ct);

        var state = await db.FetchState.AsNoTracking().SingleAsync(Ct);
        state.InboxTotal.ShouldBe(inboxTotal);
        state.AllMailTotal.ShouldBe(allMailTotal);
    }

    [Fact]
    public async Task Document_type_columns_apply_over_existing_suggestions_and_decisions()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await using var db = CreateDbContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync("M7_SuggestionReplaceLabels", cancellationToken: Ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO messages (id, thread_id, from_address, internal_date, label_ids, has_attachment, size_estimate, fetched_at, updated_at)
            VALUES ('m1', 't1', 'a@example.com', now(), ARRAY[]::text[], false, 0, now(), now());
            INSERT INTO suggestions (id, message_id, sender_address, source, topic_label, is_new_label, needs_action, to_be_deleted,
                unsubscribe_suggested, confidence, reason, status, edited, created_at)
            VALUES (gen_random_uuid(), 'm1', 'a@example.com', 'llm', 'Topic', false, false, false, false, 0.5, 'Synthetic', 'pending', false, now());
            INSERT INTO decisions (id, sender_address, topic_label, needs_action, to_be_deleted, outcome, source, edited, created_at)
            VALUES (gen_random_uuid(), 'a@example.com', 'Topic', false, false, 'approved', 'llm', false, now());
            """, Ct);

        await db.Database.MigrateAsync(Ct);

        var suggestion = await db.Suggestions.AsNoTracking().SingleAsync(Ct);
        (suggestion.DocumentTypeLabel, suggestion.DocumentTypeIsNew).ShouldBe((null, false));
        var decision = await db.Decisions.AsNoTracking().SingleAsync(Ct);
        (decision.DocumentTypeLabel, decision.DocumentTypeDecided).ShouldBe((null, false));
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
    public async Task Fetch_state_row_is_seeded_by_migration()
    {
        // A fresh database: the shared one holds whatever fetch state earlier test classes left behind.
        var connectionString = await CreateEmptyDatabaseAsync();
        await using var db = CreateDbContext(connectionString);
        await db.Database.MigrateAsync(Ct);

        var state = await db.FetchState.AsNoTracking().SingleAsync(Ct);

        state.Id.ShouldBe(FetchStateRow.SingletonId);
        state.MailboxPhase.ShouldBe(MailboxPhase.NotStarted);
        state.PageToken.ShouldBeNull();
        state.InboxFetched.ShouldBe(0);
        var phase = await db.Database
            .SqlQuery<string>($"SELECT mailbox_phase AS \"Value\" FROM fetch_state WHERE id = 1")
            .SingleAsync(Ct);
        phase.ShouldBe("not_started");
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
