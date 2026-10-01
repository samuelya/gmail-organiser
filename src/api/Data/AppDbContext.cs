using GmailOrganiser.Gmail;
using GmailOrganiser.Settings;
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
    public DbSet<SettingsRow> Settings => Set<SettingsRow>();
    public DbSet<OAuthTokenRow> OAuthTokens => Set<OAuthTokenRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<SettingsRow>(e =>
        {
            e.ToTable("settings", t => t.HasCheckConstraint("ck_settings_singleton", $"id = {SettingsRow.SingletonId}"));
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Document).HasColumnType("jsonb").IsRequired();
        });

        modelBuilder.Entity<OAuthTokenRow>(e =>
        {
            e.ToTable("oauth_tokens", t => t.HasCheckConstraint("ck_oauth_tokens_singleton", $"id = {OAuthTokenRow.SingletonId}"));
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.AccountEmail).IsRequired();
            e.Property(r => r.RefreshTokenProtected).IsRequired();
            e.Property(r => r.Scopes).IsRequired();
        });
    }

    /// <summary>Applies the provider settings shared by the app, design-time tooling and tests.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string? connectionString)
    {
        return options
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention();
    }
}
