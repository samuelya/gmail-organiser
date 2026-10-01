using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Data;

/// <summary>
/// The application's EF Core context (PostgreSQL + pgvector, snake_case names).
/// </summary>
/// <remarks>
/// Add a migration from the repository root (one per model-changing issue, named <c>M&lt;n&gt;_&lt;What&gt;</c>):
/// <code>
/// dotnet tool restore
/// dotnet ef migrations add M1_Example --project src/api --output-dir Data/Migrations
/// </code>
/// Never edit an applied migration. Migrations are applied at API start-up by <see cref="DatabaseMigrator"/>.
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
    }

    /// <summary>Applies the provider settings shared by the app, design-time tooling and tests.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string? connectionString)
    {
        return options
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention();
    }
}
