using GmailOrganiser.Jobs;

namespace GmailOrganiser.Fetch;

public sealed record StartFetchResponse(Guid JobId);

/// <param name="Target">A sender address (<c>local@domain</c>) or a bare domain.</param>
public sealed record SenderFetchRequest(string? Target);

/// <param name="MailboxPhase"><c>not_started</c>, <c>inbox</c>, <c>all_mail</c>, <c>reconcile</c> or <c>completed</c>.</param>
/// <param name="InboxTotal">The Inbox message total the current or last mailbox fetch measures progress against (never
/// below <c>InboxFetched</c>, equal to it once the phase is done); null before a run has started.</param>
/// <param name="AllMailTotal">The All Mail total (excluding Spam and Trash) of the current or last mailbox fetch;
/// null before a run has started.</param>
/// <param name="MessagesStored">Stored messages, excluding those deleted in Gmail.</param>
/// <param name="ActiveJob">The queued, running or paused mailbox or incremental fetch job, if any (<c>type</c> says which).</param>
/// <param name="FailedJob">The latest mailbox or incremental fetch job when it failed (with its error), so it can be resumed.</param>
/// <param name="AccountMismatch">The local data belongs to a different account than the connected one; fetching is blocked.</param>
/// <param name="LocalAccount">The local data's account, masked (<c>u***@example.com</c>); set only while mismatched.</param>
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
    JobDto? FailedJob,
    bool AccountMismatch,
    string? LocalAccount,
    long? InboxTotal,
    long? AllMailTotal);
