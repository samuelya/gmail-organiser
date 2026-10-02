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
    JobDto? ActiveFetchJob);
