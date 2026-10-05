using GmailOrganiser.CleanUp.Unsubscribe;

namespace GmailOrganiser.Senders;

/// <summary>Per-sender counts (<c>senders</c>), recomputed from <c>messages</c> by fetch.</summary>
public sealed class SenderRow
{
    /// <summary>Lower-case sender address.</summary>
    public string Address { get; set; } = "";

    public string Domain { get; set; } = "";

    /// <summary>
    /// The relay-decoded sender (<see cref="RelayAddressDecoder"/>), the grouping key of later features; the primary
    /// key stays the raw <see cref="Address"/>, which unsubscribe, the allowlist and filters need.
    /// </summary>
    public string CanonicalAddress { get; set; } = "";

    public string CanonicalDomain { get; set; } = "";

    /// <summary>Whether <see cref="Address"/> is a decoded relay address.</summary>
    public bool IsRelay { get; set; }
    public string? DisplayName { get; set; }
    public int TotalCount { get; set; }
    public int AnalysedCount { get; set; }
    public int AppliedCount { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>The oldest live message's date when the row's stats first saw one; never moves later.</summary>
    public DateTimeOffset? FirstSeenAt { get; set; }

    // Engagement stats over live messages, written by SenderStatsRebuildJob (stale until its next run).
    public int UnreadCount { get; set; }
    public int RepliedCount { get; set; }
    public int StarredCount { get; set; }
    public int ListUnsubscribeCount { get; set; }
    public int BulkHeaderCount { get; set; }
    public int PrimaryCount { get; set; }
    public int PromotionsCount { get; set; }
    public int SocialCount { get; set; }
    public int UpdatesCount { get; set; }
    public int ForumsCount { get; set; }

    /// <summary>The Stage-0 classification (<see cref="SenderStatsCalculator.Kind"/>) of the counts above.</summary>
    public SenderKind Kind { get; set; }

    /// <summary>When <see cref="SenderStatsRebuildJob"/> last wrote the engagement stats; null until it has.</summary>
    public DateTimeOffset? StatsAt { get; set; }
    public bool Allowlisted { get; set; }

    /// <summary>When the user last unsubscribed (one-click by the api, or a link/mailto marked by hand).</summary>
    public DateTimeOffset? UnsubscribedAt { get; set; }

    public UnsubscribeMethod? UnsubscribeMethod { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
