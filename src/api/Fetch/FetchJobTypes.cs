namespace GmailOrganiser.Fetch;

/// <summary>Job types of the Fetch feature, shared by the handlers and the endpoints that look their jobs up.</summary>
public static class FetchJobTypes
{
    public const string Mailbox = "mailbox_fetch";

    /// <summary>The per-sender fetch; its cursor's <c>target</c> is a sender address or a domain.</summary>
    public const string Sender = "sender_fetch";

    /// <summary>The <c>history.list</c> replay after a completed mailbox fetch.</summary>
    public const string Incremental = "incremental_fetch";

    /// <summary>
    /// The job types that read Gmail into the local data: each gets <see cref="FetchAccountJobGuard"/> and calls
    /// <see cref="LocalAccountClaim"/>, and a connect to another account is refused while one is active.
    /// </summary>
    public static readonly string[] ReadsGmail = [Mailbox, Sender, Incremental];
}
