namespace GmailOrganiser.Fetch;

/// <summary>
/// A message id the current mailbox fetch already listed: in its Inbox phase, so the All Mail phase skips its
/// <c>messages.get</c>, and in every phase of a resync, so the reconcile phase re-reads only the stored rows not
/// listed. Cleared when a run starts and when it completes; survives a pause or restart in between.
/// </summary>
public sealed class FetchRunMessageRow
{
    public required string MessageId { get; set; }
}
