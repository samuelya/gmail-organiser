using GmailOrganiser.Analysis;
using GmailOrganiser.Claude;
using GmailOrganiser.CleanUp.Unsubscribe;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Jobs;
using GmailOrganiser.Memory;
using GmailOrganiser.Policies;
using GmailOrganiser.Review;
using GmailOrganiser.Rules;
using GmailOrganiser.Rules.Labels;
using GmailOrganiser.Rules.Review;
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
    public DbSet<AnalysisRunRow> AnalysisRuns => Set<AnalysisRunRow>();
    public DbSet<SuggestionRow> Suggestions => Set<SuggestionRow>();
    public DbSet<SuggestionAlternativeRow> SuggestionAlternatives => Set<SuggestionAlternativeRow>();
    public DbSet<DecisionRow> Decisions => Set<DecisionRow>();
    public DbSet<ActionBatchRow> ActionBatches => Set<ActionBatchRow>();
    public DbSet<ActionLogRow> ActionLog => Set<ActionLogRow>();
    public DbSet<ExternalReviewRow> ExternalReviews => Set<ExternalReviewRow>();
    public DbSet<FilterRow> Filters => Set<FilterRow>();
    public DbSet<LabelPlanRow> LabelPlans => Set<LabelPlanRow>();
    public DbSet<FilterReviewRow> FilterReviews => Set<FilterReviewRow>();
    public DbSet<FilterFindingRow> FilterFindings => Set<FilterFindingRow>();
    public DbSet<SenderPolicyRow> SenderPolicies => Set<SenderPolicyRow>();
    public DbSet<SenderPolicyRuleRow> SenderPolicyRules => Set<SenderPolicyRuleRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("pg_trgm");

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
            e.Property(r => r.CanonicalAddress).IsRequired();
            e.Property(r => r.CanonicalDomain).IsRequired();
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
            // The replied-thread check reads and writes a thread's rows.
            e.HasIndex(r => r.ThreadId);
            e.HasIndex(r => r.CanonicalAddress);
        });

        modelBuilder.Entity<SenderRow>(e =>
        {
            e.ToTable("senders");
            e.HasKey(r => r.Address);
            e.Property(r => r.Address).ValueGeneratedNever();
            e.Property(r => r.Domain).IsRequired();
            e.Property(r => r.CanonicalAddress).IsRequired();
            e.Property(r => r.CanonicalDomain).IsRequired();
            e.Property(r => r.IsRelay).HasDefaultValue(false);
            e.Property(r => r.Allowlisted).HasDefaultValue(false);
            e.Property(r => r.UnsubscribeMethod).HasConversion(new SnakeCaseEnumConverter<UnsubscribeMethod>());
            e.HasIndex(r => r.Domain);
            e.HasIndex(r => r.CanonicalAddress);
            e.HasIndex(r => r.CanonicalDomain);
            // Trigram indexes for SenderQuery's ILIKE '%term%' search (a leading wildcard can't use a B-tree).
            e.HasIndex(r => r.Address, "ix_senders_address_trgm").HasDatabaseName("ix_senders_address_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(r => r.Domain, "ix_senders_domain_trgm").HasDatabaseName("ix_senders_domain_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(r => r.CanonicalAddress, "ix_senders_canonical_address_trgm").HasDatabaseName("ix_senders_canonical_address_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(r => r.CanonicalDomain, "ix_senders_canonical_domain_trgm").HasDatabaseName("ix_senders_canonical_domain_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
            e.HasIndex(r => r.DisplayName, "ix_senders_display_name_trgm").HasDatabaseName("ix_senders_display_name_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
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
        AnalysisRunRow.Configure(modelBuilder);
        SuggestionRow.Configure(modelBuilder);
        SuggestionAlternativeRow.Configure(modelBuilder);
        DecisionRow.Configure(modelBuilder);
        ActionBatchRow.Configure(modelBuilder);
        ActionLogRow.Configure(modelBuilder);
        ExternalReviewRow.Configure(modelBuilder);
        FilterRow.Configure(modelBuilder);
        LabelPlanRow.Configure(modelBuilder);
        FilterReviewRow.Configure(modelBuilder);
        SenderPolicyRow.Configure(modelBuilder);
        SenderPolicyRuleRow.Configure(modelBuilder);
    }

    /// <summary>Applies the provider settings shared by the app, design-time tooling and tests.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string? connectionString)
    {
        return options
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention();
    }
}
