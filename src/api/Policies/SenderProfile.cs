using GmailOrganiser.Fetch;
using GmailOrganiser.Senders;

namespace GmailOrganiser.Policies;

/// <summary>
/// A compact description of one sender, domain or mailing list (DESIGN §6.2): the policy and taxonomy prompts describe
/// a sender by this instead of by its individual emails. Built by <see cref="SenderProfileBuilder"/>; never stored.
/// </summary>
/// <param name="ScopeKey">The canonical address, canonical domain or normalised List-Id.</param>
/// <param name="DisplayNames">The most common display names, at most three.</param>
/// <param name="Addresses">The most common raw sender addresses, at most ten.</param>
/// <param name="Templates">The most common subject templates, at most <see cref="SenderProfileBuilder.ProfileMaxTemplates"/>.</param>
/// <param name="OtherTemplates">Templates beyond <paramref name="Templates"/>.</param>
/// <param name="OtherTemplateMessages">Messages of <paramref name="OtherTemplates"/>.</param>
/// <param name="Bodies">Cleaned example bodies, only when asked for; read from Gmail, never stored.</param>
/// <param name="LabelsInUse">User labels on the sender's mail, at most five.</param>
/// <param name="ApprovedPolicyHints">Approved policies of other senders of the same canonical domain.</param>
public sealed record SenderProfile(
    PolicyScope Scope,
    string ScopeKey,
    IReadOnlyList<string> DisplayNames,
    IReadOnlyList<string> Addresses,
    SenderProfileStats Stats,
    IReadOnlyList<SenderTemplate> Templates,
    int OtherTemplates,
    int OtherTemplateMessages,
    IReadOnlyList<ProfileBody> Bodies,
    IReadOnlyList<LabelUse> LabelsInUse,
    IReadOnlyList<PolicyHint> ApprovedPolicyHints);

/// <summary>Counts over all live messages in scope; ratios are 0–1 of <see cref="Total"/>.</summary>
/// <param name="Allowlisted">An address of the profile, or its domain, is allowlisted.</param>
public sealed record SenderProfileStats(
    int Total,
    double UnreadRatio,
    int Replied,
    int Starred,
    double ListUnsubscribeRatio,
    double BulkHeaderRatio,
    CategoryMix CategoryMix,
    SenderKind Kind,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    bool Allowlisted);

/// <summary>Messages per Gmail category tab; <see cref="None"/> counts messages without one.</summary>
public sealed record CategoryMix(int Primary, int Promotions, int Social, int Updates, int Forums, int None)
{
    /// <summary>The category with the most messages (null for <see cref="None"/>); ties go to the earlier one in declaration order.</summary>
    public MessageCategory? Dominant =>
        new (MessageCategory? Category, int Count)[]
            {
                (MessageCategory.Primary, Primary), (MessageCategory.Promotions, Promotions), (MessageCategory.Social, Social),
                (MessageCategory.Updates, Updates), (MessageCategory.Forums, Forums), (null, None),
            }
            .Aggregate((best, next) => next.Count > best.Count ? next : best).Category;

    public static CategoryMix Of(IEnumerable<MessageCategory?> categories)
    {
        var list = categories.ToList();
        return new CategoryMix(
            list.Count(c => c == MessageCategory.Primary),
            list.Count(c => c == MessageCategory.Promotions),
            list.Count(c => c == MessageCategory.Social),
            list.Count(c => c == MessageCategory.Updates),
            list.Count(c => c == MessageCategory.Forums),
            list.Count(c => c is null));
    }
}

/// <summary>One subject template (<see cref="Analysis.Grouping.SubjectNormaliser.Template"/>) with its header signals.</summary>
/// <param name="ExampleSubject">The newest subject of the template.</param>
/// <param name="ListIdPresent">At least half of the template's messages carry a <c>List-Id</c>.</param>
/// <param name="ListUnsubscribePresent">At least half carry <c>List-Unsubscribe</c>.</param>
/// <param name="Precedence">The most common <c>Precedence</c> header, if any message has one.</param>
public sealed record SenderTemplate(
    string Template,
    int Count,
    string? ExampleSubject,
    bool ListIdPresent,
    bool ListUnsubscribePresent,
    CategoryMix CategoryMix,
    double AttachmentRatio,
    double UnreadRatio,
    string? Precedence);

/// <summary>The cleaned, truncated body of one example message of <see cref="Template"/>.</summary>
public sealed record ProfileBody(string Template, string Text);

public sealed record LabelUse(string Label, int Count);

/// <summary>An approved policy of the same canonical domain: what the user already decided for a sibling sender.</summary>
public sealed record PolicyHint(PolicyScope Scope, string ScopeKey, bool IsMixed, string? TopicLabel, string? DocumentTypeLabel, PolicyAction Action);
