using GmailOrganiser.Jobs;

namespace GmailOrganiser.Fetch;

public sealed record StartFetchResponse(Guid JobId);

/// <param name="MailboxPhase"><c>not_started</c>, <c>inbox</c>, <c>all_mail</c> or <c>completed</c>.</param>
/// <param name="MessagesStored">Stored messages, excluding those deleted in Gmail.</param>
/// <param name="ActiveJob">The queued, running or paused mailbox fetch job, if any.</param>
/// <param name="FailedJob">The latest mailbox fetch job when it failed (with its error), so it can be resumed.</param>
public sealed record FetchStatusDto(
    string? AccountEmail,
    string MailboxPhase,
    int InboxFetched,
    int AllMailFetched,
    long? MessagesTotal,
    long MessagesStored,
    long SendersCount,
    string? LastHistoryId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    JobDto? ActiveJob,
    JobDto? FailedJob);
