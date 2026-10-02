namespace GmailOrganiser.Fetch;

/// <summary>Job types of the Fetch feature, shared by the handlers and the endpoints that look their jobs up.</summary>
public static class FetchJobTypes
{
    public const string Mailbox = "mailbox_fetch";

    /// <summary>The per-sender fetch; its cursor's <c>target</c> is a sender address or a domain.</summary>
    public const string Sender = "sender_fetch";
}
