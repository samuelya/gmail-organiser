using GmailOrganiser.Jobs;

namespace GmailOrganiser.Senders;

/// <param name="ActiveFetchJob">The queued, running or paused <c>sender_fetch</c> job targeting this address or its domain.</param>
public sealed record SenderDto(
    string Address,
    string Domain,
    string? DisplayName,
    int TotalCount,
    int AnalysedCount,
    int AppliedCount,
    DateTimeOffset? LastSeenAt,
    bool Allowlisted,
    JobDto? ActiveFetchJob,
    DateTimeOffset? UnsubscribedAt);

/// <param name="Allowlisted">Required; nullable only so a missing value is a 400 rather than <c>false</c>.</param>
public sealed record AllowlistRequest(bool? Allowlisted);
