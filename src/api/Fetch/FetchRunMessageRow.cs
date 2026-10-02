namespace GmailOrganiser.Fetch;

/// <summary>
/// A message id the current mailbox fetch already stored in its Inbox phase, so the All Mail phase skips its
/// <c>messages.get</c>. Cleared when a run starts and when it completes; survives a pause or restart in between.
/// </summary>
public sealed class FetchRunMessageRow
{
    public required string MessageId { get; set; }
}
