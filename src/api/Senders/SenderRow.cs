namespace GmailOrganiser.Senders;

/// <summary>Per-sender counts (<c>senders</c>), recomputed from <c>messages</c> by fetch.</summary>
public sealed class SenderRow
{
    /// <summary>Lower-case sender address.</summary>
    public string Address { get; set; } = "";

    public string Domain { get; set; } = "";
    public string? DisplayName { get; set; }
    public int TotalCount { get; set; }
    public int AnalysedCount { get; set; }
    public int AppliedCount { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool Allowlisted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
