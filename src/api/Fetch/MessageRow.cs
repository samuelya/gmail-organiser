namespace GmailOrganiser.Fetch;

/// <summary>Gmail's inbox category tab, derived from the message's <c>CATEGORY_*</c> label.</summary>
public enum MessageCategory
{
    Primary,
    Social,
    Promotions,
    Updates,
    Forums,
}

/// <summary>
/// A message's analysis status. Transition order:
/// <c>NotAnalysed → Analysed → Approved → Applied</c>, with <c>Analysed → Rejected</c> as a terminal branch;
/// re-analyse resets to <c>NotAnalysed</c>. Mirrors <c>SuggestionRow.Status</c>, which is authoritative.
/// </summary>
public enum AnalysisStatus
{
    NotAnalysed,
    Analysed,
    Approved,
    Rejected,
    Applied,
}

/// <summary>
/// One Gmail message's metadata (<c>messages</c>). Bodies are never stored. <see cref="FromAddress"/> joins to
/// <c>senders.address</c> without a foreign key, so fetch can upsert messages before recomputing senders.
/// </summary>
public sealed class MessageRow
{
    /// <summary>The Gmail message ID.</summary>
    public string Id { get; set; } = "";

    public string ThreadId { get; set; } = "";
    public string? HistoryId { get; set; }

    /// <summary>Lower-case sender address.</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>The relay-decoded sender (<see cref="Senders.RelayAddressDecoder"/>); <see cref="FromAddress"/> when not a relay.</summary>
    public string CanonicalAddress { get; set; } = "";

    public string CanonicalDomain { get; set; } = "";

    public string? FromName { get; set; }
    public string? ToHeader { get; set; }
    public string? Subject { get; set; }
    public DateTimeOffset InternalDate { get; set; }
    public string[] LabelIds { get; set; } = [];
    public MessageCategory? Category { get; set; }
    public bool HasAttachment { get; set; }
    public int SizeEstimate { get; set; }
    public string? Snippet { get; set; }
    public string? ListId { get; set; }
    public string? ListUnsubscribe { get; set; }
    public AnalysisStatus AnalysisStatus { get; set; } = AnalysisStatus.NotAnalysed;
    /// <summary>
    /// Not live: gone from Gmail or in Trash. Every writer keeps a row carrying <c>TRASH</c> at true, so readers
    /// (stats, analysis, apply, patterns, MCP) filter on this alone.
    /// </summary>
    public bool DeletedInGmail { get; set; }

    /// <summary>
    /// Whether any message of the thread carries <c>SENT</c> (#177): null until checked at mark time; <c>true</c> is
    /// final, a stored <c>false</c> goes back to null when a new message arrives in the thread.
    /// </summary>
    public bool? ThreadReplied { get; set; }

    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
