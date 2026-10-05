using GmailOrganiser.Data;

namespace GmailOrganiser.Policies;

/// <summary>A <see cref="SenderProfile"/> for the API; enums are snake_case strings.</summary>
/// <param name="Scope"><c>sender</c>, <c>domain</c> or <c>list</c>.</param>
/// <param name="Bodies">Empty unless <c>includeBodies=true</c>.</param>
/// <param name="PromptText">The profile as the prompts embed it (<see cref="SenderProfileBuilder.ToPromptText"/>).</param>
public sealed record SenderProfileDto(
    string Scope,
    string ScopeKey,
    IReadOnlyList<string> DisplayNames,
    IReadOnlyList<string> Addresses,
    SenderProfileStatsDto Stats,
    IReadOnlyList<SenderTemplateDto> Templates,
    int OtherTemplates,
    int OtherTemplateMessages,
    IReadOnlyList<ProfileBody> Bodies,
    IReadOnlyList<LabelUse> LabelsInUse,
    IReadOnlyList<PolicyHintDto> ApprovedPolicyHints,
    string PromptText)
{
    public static SenderProfileDto From(SenderProfile p) => new(
        SnakeCaseEnumConverter<PolicyScope>.ToDb(p.Scope),
        p.ScopeKey,
        p.DisplayNames,
        p.Addresses,
        SenderProfileStatsDto.From(p.Stats),
        [.. p.Templates.Select(SenderTemplateDto.From)],
        p.OtherTemplates,
        p.OtherTemplateMessages,
        p.Bodies,
        p.LabelsInUse,
        [.. p.ApprovedPolicyHints.Select(PolicyHintDto.From)],
        SenderProfileBuilder.ToPromptText(p));
}

/// <param name="Kind"><c>unknown</c>, <c>human</c>, <c>bulk</c> or <c>mixed</c>.</param>
public sealed record SenderProfileStatsDto(
    int Total,
    double UnreadRatio,
    int Replied,
    int Starred,
    double ListUnsubscribeRatio,
    double BulkHeaderRatio,
    CategoryMixDto CategoryMix,
    string Kind,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    bool Allowlisted)
{
    public static SenderProfileStatsDto From(SenderProfileStats s) => new(
        s.Total, s.UnreadRatio, s.Replied, s.Starred, s.ListUnsubscribeRatio, s.BulkHeaderRatio, CategoryMixDto.From(s.CategoryMix),
        SnakeCaseEnumConverter<Senders.SenderKind>.ToDb(s.Kind), s.FirstSeen, s.LastSeen, s.Allowlisted);
}

public sealed record CategoryMixDto(int Primary, int Promotions, int Social, int Updates, int Forums, int None)
{
    public static CategoryMixDto From(CategoryMix m) => new(m.Primary, m.Promotions, m.Social, m.Updates, m.Forums, m.None);
}

/// <param name="Precedence">The most common <c>Precedence</c> header, or null.</param>
public sealed record SenderTemplateDto(
    string Template,
    int Count,
    string? ExampleSubject,
    bool ListIdPresent,
    bool ListUnsubscribePresent,
    CategoryMixDto CategoryMix,
    double AttachmentRatio,
    double UnreadRatio,
    string? Precedence)
{
    public static SenderTemplateDto From(SenderTemplate t) => new(
        t.Template, t.Count, t.ExampleSubject, t.ListIdPresent, t.ListUnsubscribePresent, CategoryMixDto.From(t.CategoryMix),
        t.AttachmentRatio, t.UnreadRatio, t.Precedence);
}

/// <param name="Action"><c>keep</c>, <c>archive</c>, <c>delete</c> or <c>unsubscribe</c>.</param>
public sealed record PolicyHintDto(string Scope, string ScopeKey, bool IsMixed, string? TopicLabel, string? DocumentTypeLabel, string Action)
{
    public static PolicyHintDto From(PolicyHint h) => new(
        SnakeCaseEnumConverter<PolicyScope>.ToDb(h.Scope), h.ScopeKey, h.IsMixed, h.TopicLabel, h.DocumentTypeLabel,
        SnakeCaseEnumConverter<PolicyAction>.ToDb(h.Action));
}

/// <summary>A sender policy in the review list; enums are snake_case strings.</summary>
/// <param name="MessageCount">The senders rows' message count (canonical address or domain); live messages for a list.</param>
/// <param name="Kind">The senders rows' Stage-0 kind (<c>unknown</c>, <c>human</c>, <c>bulk</c>, <c>mixed</c>); a list is bulk.</param>
/// <param name="UnreadRatio">0–1 of <paramref name="MessageCount"/>.</param>
public sealed record SenderPolicyDto(
    Guid Id,
    string Scope,
    string ScopeKey,
    string? DisplayName,
    bool IsMixed,
    string? TopicLabel,
    string? DocumentTypeLabel,
    string? MailType,
    int? RetentionDays,
    string Action,
    double Confidence,
    string Reason,
    string? Model,
    string? PromptVersion,
    string Status,
    bool Edited,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? AppliedAt,
    int RuleCount,
    int MessageCount,
    string Kind,
    double UnreadRatio);

/// <summary>A <see cref="RuleMatch"/> on the wire; <paramref name="Category"/> is a snake_case <c>MessageCategory</c>.</summary>
public sealed record RuleMatchDto(
    bool? ListIdPresent = null,
    bool? ListUnsubscribePresent = null,
    string? FromAddress = null,
    string? FromSubdomain = null,
    string? Category = null,
    string? SubjectTemplate = null,
    string? SubjectContains = null)
{
    public static RuleMatchDto From(RuleMatch m) => new(
        m.ListIdPresent, m.ListUnsubscribePresent, m.FromAddress, m.FromSubdomain,
        m.Category is { } c ? SnakeCaseEnumConverter<Fetch.MessageCategory>.ToDb(c) : null, m.SubjectTemplate, m.SubjectContains);
}

/// <summary>One sub-rule in saved order with its preview over the policy's sample (<see cref="SenderPolicyDetailDto"/>).</summary>
/// <param name="MatchCount">Sample messages this rule decides; 0 for a rejected rule.</param>
/// <param name="SampleSubjects">The newest of them, at most three.</param>
public sealed record PolicyRuleDto(
    Guid Id,
    int Position,
    string Name,
    RuleMatchDto Match,
    string TopicLabel,
    string? DocumentTypeLabel,
    string? MailType,
    int? RetentionDays,
    string Action,
    string Status,
    string Source,
    string Reason,
    int MatchCount,
    IReadOnlyList<string> SampleSubjects);

/// <summary>
/// A policy with a preview of what it would do: <see cref="PolicyMatcher"/> over the scope's newest
/// <see cref="PolicyQuery.PreviewMaxMessages"/> live messages, proposed rules counted as if approved. Every sampled
/// message is in exactly one count: a rule's, <paramref name="DefaultCount"/>, <paramref name="GuardedCount"/> or
/// <paramref name="UnmatchedCount"/>.
/// </summary>
/// <param name="DefaultCount">Messages the policy default decides (never for a mixed sender).</param>
/// <param name="UnmatchedCount">Messages of a mixed sender no rule matches: they go to review.</param>
/// <param name="GuardedCount">Transactional messages the guard keeps from a delete: archived instead, or sent to review for a mixed sender.</param>
/// <param name="SampledMessages">The messages previewed.</param>
/// <param name="Sampled">The scope has more live messages than were previewed.</param>
/// <param name="ProfileTemplates">The sender profile's subject templates (#355), without bodies.</param>
public sealed record SenderPolicyDetailDto(
    SenderPolicyDto Policy,
    IReadOnlyList<PolicyRuleDto> Rules,
    int DefaultCount,
    int UnmatchedCount,
    int GuardedCount,
    int SampledMessages,
    bool Sampled,
    IReadOnlyList<SenderTemplateDto> ProfileTemplates);

/// <param name="Id">An existing rule of the policy to keep (its status, source and reason stay); null for a new rule.</param>
/// <param name="MailType">A snake_case <c>MailType</c>, or null.</param>
/// <param name="Action"><c>keep</c>, <c>archive</c>, <c>delete</c> or <c>unsubscribe</c>.</param>
public sealed record EditPolicyRuleRequest(
    Guid? Id,
    string? Name,
    RuleMatchDto? Match,
    string? TopicLabel,
    string? DocumentTypeLabel,
    string? MailType,
    int? RetentionDays,
    string? Action);

/// <summary>Replaces the policy's fields and its whole rule list; rule positions are the array order.</summary>
/// <param name="DocumentTypeLabel">Null or blank for none; otherwise under the configured document-type parent.</param>
public sealed record EditPolicyRequest(
    string? TopicLabel,
    string? DocumentTypeLabel,
    string? MailType,
    int? RetentionDays,
    string? Action,
    bool? IsMixed,
    EditPolicyRuleRequest[]? Rules);

/// <param name="Reapply">The policy was already approved: its changes reach the mail only when it is applied again (#359).</param>
public sealed record EditPolicyResponse(SenderPolicyDetailDto Policy, bool Reapply);

/// <param name="JobId">The queued <see cref="PolicyApplyJob"/>.</param>
public sealed record ApprovePolicyResponse(SenderPolicyDto Policy, Guid? JobId);
