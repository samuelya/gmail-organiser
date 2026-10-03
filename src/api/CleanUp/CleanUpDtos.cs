using GmailOrganiser.Review;

namespace GmailOrganiser.CleanUp;

/// <summary>Stored messages carrying the delete label, not deleted in Gmail; all 0 when the label does not exist.</summary>
/// <param name="Protected">Of <paramref name="Messages"/>, those Delete skips unless asked to include them.</param>
public sealed record CleanupSummaryDto(int Messages, int Senders, int Protected);

public sealed record CleanupSenderDto(
    string Address,
    string? DisplayName,
    int Count,
    int ProtectedCount,
    DateTimeOffset? OldestAt,
    DateTimeOffset? NewestAt,
    bool Allowlisted);

/// <param name="ProtectedReason">Why Delete skips the message (<c>MessageProtection.Reason</c>); null when it does not.</param>
public sealed record CleanupMessageDto(
    string Id,
    string? Subject,
    string? Snippet,
    DateTimeOffset InternalDate,
    int SizeEstimate,
    bool InInbox,
    string? ProtectedReason);

/// <summary>Exactly one of <paramref name="MessageIds"/>, <paramref name="SenderAddress"/> or <paramref name="All"/>.</summary>
/// <param name="IncludeProtected">Delete only: also trash protected messages. Unmark always includes them.</param>
public sealed record CleanupSelectionRequest(
    string[]? MessageIds = null, string? SenderAddress = null, bool All = false, bool IncludeProtected = false);

/// <param name="Queued">Messages the job will change.</param>
/// <param name="SkippedProtected">Selected messages Delete leaves alone because they are protected.</param>
public sealed record CleanupBatchDto(ActionBatchDto Batch, int Queued, int SkippedProtected);
