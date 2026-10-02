using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Senders;
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
    public DbSet<MessageRow> Messages => Set<MessageRow>();
    public DbSet<SenderRow> Senders => Set<SenderRow>();
    public DbSet<FetchStateRow> FetchState => Set<FetchStateRow>();
    public DbSet<FetchRunMessageRow> FetchRunMessages => Set<FetchRunMessageRow>();
    public DbSet<JobRow> Jobs => Set<JobRow>();

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

        modelBuilder.Entity<MessageRow>(e =>
        {
            e.ToTable("messages");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.ThreadId).IsRequired();
            e.Property(r => r.FromAddress).IsRequired();
            e.Property(r => r.LabelIds).IsRequired();
            e.Property(r => r.Category).HasConversion(new SnakeCaseEnumConverter<MessageCategory>());
            e.Property(r => r.AnalysisStatus)
                .HasConversion(new SnakeCaseEnumConverter<AnalysisStatus>())
                .HasDefaultValue(AnalysisStatus.NotAnalysed);
            e.Property(r => r.DeletedInGmail).HasDefaultValue(false);
            // Sender stats count, max and sort a sender's messages by date.
            e.HasIndex(r => new { r.FromAddress, r.InternalDate });
            e.HasIndex(r => r.InternalDate);
            e.HasIndex(r => r.LabelIds).HasMethod("gin");
            e.HasIndex(r => r.AnalysisStatus);
        });

        modelBuilder.Entity<SenderRow>(e =>
        {
            e.ToTable("senders");
            e.HasKey(r => r.Address);
            e.Property(r => r.Address).ValueGeneratedNever();
            e.Property(r => r.Domain).IsRequired();
            e.Property(r => r.Allowlisted).HasDefaultValue(false);
            e.HasIndex(r => r.Domain);
        });

        modelBuilder.Entity<FetchStateRow>(e =>
        {
            e.ToTable("fetch_state", t => t.HasCheckConstraint("ck_fetch_state_singleton", $"id = {FetchStateRow.SingletonId}"));
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.MailboxPhase).HasConversion(new SnakeCaseEnumConverter<MailboxPhase>());
            e.HasData(new FetchStateRow { Id = FetchStateRow.SingletonId, UpdatedAt = DateTimeOffset.UnixEpoch });
        });

        modelBuilder.Entity<FetchRunMessageRow>(e =>
        {
            e.ToTable("fetch_run_messages");
            e.HasKey(r => r.MessageId);
            e.Property(r => r.MessageId).ValueGeneratedNever();
        });

        JobRow.Configure(modelBuilder);
    }

    /// <summary>Applies the provider settings shared by the app, design-time tooling and tests.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string? connectionString)
    {
        return options
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention();
    }
}
