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
