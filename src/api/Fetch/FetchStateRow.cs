namespace GmailOrganiser.Fetch;

/// <summary>Where the mailbox fetch is: inbox first, then the rest of All Mail.</summary>
public enum MailboxPhase
{
    NotStarted,
    Inbox,
    AllMail,
    Completed,
}

/// <summary>
/// The single <c>fetch_state</c> row (<see cref="Id"/> is always <see cref="SingletonId"/>), seeded by the migration
/// so readers never handle a missing row. Per-sender fetches are jobs, not part of this row.
/// </summary>
public sealed class FetchStateRow
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string? AccountEmail { get; set; }
    public MailboxPhase MailboxPhase { get; set; } = MailboxPhase.NotStarted;

    /// <summary>The Gmail list page token to resume the current phase from.</summary>
    public string? PageToken { get; set; }

    public int InboxFetched { get; set; }
    public int AllMailFetched { get; set; }

    /// <summary>The mailbox total from the last Gmail profile call.</summary>
    public long? MessagesTotal { get; set; }

    public string? LastHistoryId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
