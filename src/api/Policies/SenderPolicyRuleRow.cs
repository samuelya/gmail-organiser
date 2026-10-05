using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Policies;

/// <summary>
/// What a sub-rule tests; every set field must hold (<see cref="PolicyMatcher"/>), and at least one is set
/// (<see cref="PolicyValidation"/>). Stored whole as jsonb and never queried by field.
/// </summary>
public sealed class RuleMatch
{
    public bool? ListIdPresent { get; set; }
    public bool? ListUnsubscribePresent { get; set; }

    /// <summary>Compared with the canonical and the raw sender address, case-insensitively.</summary>
    public string? FromAddress { get; set; }

    /// <summary>The raw sender domain or a domain under it, e.g. <c>news.example.com</c>.</summary>
    public string? FromSubdomain { get; set; }
    public MessageCategory? Category { get; set; }

    /// <summary>Compared with <c>SubjectNormaliser.Template</c> of the subject.</summary>
    public string? SubjectTemplate { get; set; }

    /// <summary>A case-insensitive substring of the subject.</summary>
    public string? SubjectContains { get; set; }

    public bool IsEmpty =>
        ListIdPresent is null && ListUnsubscribePresent is null && Category is null
        && string.IsNullOrWhiteSpace(FromAddress) && string.IsNullOrWhiteSpace(FromSubdomain)
        && string.IsNullOrWhiteSpace(SubjectTemplate) && string.IsNullOrWhiteSpace(SubjectContains);
}

/// <summary>
/// One ordered sub-rule of a mixed sender's policy (<c>sender_policy_rules</c>); removed with its policy. Only
/// <see cref="PolicyStatus.Approved"/> rules ever match.
/// </summary>
public sealed class SenderPolicyRuleRow
{
    public Guid Id { get; set; }
    public Guid PolicyId { get; set; }

    /// <summary>The stored order; <see cref="PolicyMatcher.CostOrder"/> keeps it within a cost class.</summary>
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public RuleMatch Match { get; set; } = new();
    public string TopicLabel { get; set; } = "";
    public string? DocumentTypeLabel { get; set; }
    public MailType? MailType { get; set; }

    /// <summary>Days to keep matching mail; null = the default for the mail type.</summary>
    public int? RetentionDays { get; set; }
    public PolicyAction Action { get; set; }
    public PolicyStatus Status { get; set; }
    public PolicyRuleSource Source { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SenderPolicyRuleRow>(e =>
        {
            e.ToTable("sender_policy_rules", t =>
            {
                // Backstop for PolicyValidation: a rule with no criteria would match all of the sender's mail.
                t.HasCheckConstraint("ck_sender_policy_rules_match", "jsonb_strip_nulls(match) <> '{}'::jsonb");
                t.HasCheckConstraint("ck_sender_policy_rules_retention", "retention_days IS NULL OR retention_days > 0");
            });
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Name).IsRequired();
            e.Property(r => r.TopicLabel).IsRequired();
            e.Property(r => r.MailType).HasConversion(new SnakeCaseEnumConverter<MailType>());
            e.Property(r => r.Action).IsRequired().HasConversion(new SnakeCaseEnumConverter<PolicyAction>());
            e.Property(r => r.Status).IsRequired().HasConversion(new SnakeCaseEnumConverter<PolicyStatus>());
            e.Property(r => r.Source).IsRequired().HasConversion(new SnakeCaseEnumConverter<PolicyRuleSource>());
            e.Property(r => r.Reason).IsRequired();
            e.OwnsOne(r => r.Match, m =>
            {
                m.ToJson("match");
                m.Property(x => x.Category).HasConversion(new SnakeCaseEnumConverter<MessageCategory>());
            });
            e.Navigation(r => r.Match).IsRequired();
            e.HasIndex(r => new { r.PolicyId, r.Position });
        });
    }
}
