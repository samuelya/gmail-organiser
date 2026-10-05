using System.Text.Json;
using System.Text.Json.Serialization;
using GmailOrganiser.Jobs;

namespace GmailOrganiser.Senders;

/// <param name="Allowlisted">The address itself is allowlisted (#178).</param>
/// <param name="AllowlistedByDomain">The sender's domain, or a parent domain, is in <c>protection.allowlistedDomains</c> (#203).</param>
/// <param name="ActiveFetchJob">The queued, running or paused <c>sender_fetch</c> job targeting this address or its domain.</param>
/// <param name="CanonicalAddress">The relay-decoded sender (#345); equals <paramref name="Address"/> unless <paramref name="IsRelay"/>.</param>
/// <param name="Kind">The Stage-0 classification; the stats fields below are as of the last stats rebuild (#347).</param>
public sealed record SenderDto(
    string Address,
    string Domain,
    string? DisplayName,
    int TotalCount,
    int AnalysedCount,
    int AppliedCount,
    DateTimeOffset? LastSeenAt,
    bool Allowlisted,
    bool AllowlistedByDomain,
    JobDto? ActiveFetchJob,
    DateTimeOffset? UnsubscribedAt,
    string CanonicalAddress,
    string CanonicalDomain,
    bool IsRelay,
    SenderKind Kind,
    int UnreadCount,
    int RepliedCount,
    DateTimeOffset? FirstSeenAt,
    int ListUnsubscribeCount,
    SenderCategoryMixDto CategoryMix);

/// <summary>A sender's live messages per Gmail category tab.</summary>
public sealed record SenderCategoryMixDto(int Primary, int Promotions, int Social, int Updates, int Forums);

/// <summary>A canonical sender on the noisy-senders screen; the counts are sums over its raw addresses.</summary>
/// <param name="Addresses">The raw addresses behind <paramref name="CanonicalAddress"/>, highest volume first (at most 20).</param>
/// <param name="HasApprovedPolicy">Always false until sender policies exist (#358); kept so the UI need not change.</param>
public sealed record NoisySenderDto(
    string CanonicalAddress,
    string CanonicalDomain,
    string? DisplayName,
    IReadOnlyList<string> Addresses,
    int TotalCount,
    int UnreadCount,
    double UnreadRatio,
    int ListUnsubscribeCount,
    [property: JsonConverter(typeof(SenderKindJsonConverter))] SenderKind Kind,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    SenderCategoryMix CategoryMix,
    DateTimeOffset? UnsubscribedAt,
    bool HasApprovedPolicy);

/// <summary>Message counts per Gmail category tab.</summary>
public sealed record SenderCategoryMix(int Primary, int Promotions, int Social, int Updates, int Forums);

/// <summary>Serialises <see cref="SenderKind"/> as its snake_case name, as stored in <c>senders.kind</c>.</summary>
public sealed class SenderKindJsonConverter()
    : JsonStringEnumConverter<SenderKind>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);

/// <param name="Allowlisted">Required; nullable only so a missing value is a 400 rather than <c>false</c>.</param>
public sealed record AllowlistRequest(bool? Allowlisted);
