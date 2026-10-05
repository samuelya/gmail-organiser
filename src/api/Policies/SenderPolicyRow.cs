using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Policies;

/// <summary>
/// One standing decision per canonical sender, domain or List-Id (<c>sender_policies</c>, DESIGN §6.2): it covers the
/// sender's past and future mail without the LLM. A mixed sender's default never deletes; its <see cref="Rules"/> sort
/// its mail and whatever no rule matches goes to review (<see cref="PolicyMatcher"/>).
/// </summary>
public sealed class SenderPolicyRow
{
    public Guid Id { get; set; }
    public PolicyScope Scope { get; set; }

    /// <summary>The canonical address, canonical domain or normalised List-Id; unique with <see cref="Scope"/>.</summary>
    public string ScopeKey { get; set; } = "";
    public string? DisplayName { get; set; }

    /// <summary>The sender sends several kinds of mail; its <see cref="Rules"/> decide per message.</summary>
    public bool IsMixed { get; set; }

    /// <summary>Null only while <see cref="IsMixed"/> (<c>ck_sender_policies_topic</c>).</summary>
    public string? TopicLabel { get; set; }
    public string? DocumentTypeLabel { get; set; }
    public MailType? MailType { get; set; }

    /// <summary>Days to keep matching mail; null = the default for the mail type.</summary>
    public int? RetentionDays { get; set; }
    public PolicyAction Action { get; set; }

    /// <summary>0–1 (<c>ck_sender_policies_confidence</c>).</summary>
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public PolicyStatus Status { get; set; }
    public Guid? RunId { get; set; }

    /// <summary>The user changed the policy before deciding.</summary>
    public bool Edited { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }

    public List<SenderPolicyRuleRow> Rules { get; set; } = [];

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SenderPolicyRow>(e =>
        {
            e.ToTable("sender_policies", t =>
            {
                t.HasCheckConstraint("ck_sender_policies_confidence", "confidence BETWEEN 0 AND 1");
                t.HasCheckConstraint("ck_sender_policies_topic", "is_mixed OR topic_label IS NOT NULL");
                t.HasCheckConstraint("ck_sender_policies_retention", "retention_days IS NULL OR retention_days > 0");
            });
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Scope).IsRequired().HasConversion(new SnakeCaseEnumConverter<PolicyScope>());
            e.Property(r => r.ScopeKey).IsRequired();
            e.Property(r => r.MailType).HasConversion(new SnakeCaseEnumConverter<MailType>());
            e.Property(r => r.Action).IsRequired().HasConversion(new SnakeCaseEnumConverter<PolicyAction>());
            e.Property(r => r.Reason).IsRequired();
            e.Property(r => r.Status).IsRequired().HasConversion(new SnakeCaseEnumConverter<PolicyStatus>());
            e.HasOne<AnalysisRunRow>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(r => r.Rules).WithOne().HasForeignKey(r => r.PolicyId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.Scope, r.ScopeKey }).IsUnique();
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.RunId);
        });
    }
}
