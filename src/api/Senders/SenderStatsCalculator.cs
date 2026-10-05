using System.Text.Json.Serialization;
using GmailOrganiser.Rules.Labels;

namespace GmailOrganiser.Senders;

/// <summary>What a sender's engagement stats say it is (<c>senders.kind</c>); see <see cref="SenderStatsCalculator.Kind"/>.</summary>
[JsonConverter(typeof(SnakeCaseJsonConverter<SenderKind>))]
public enum SenderKind
{
    Unknown,
    Human,
    Bulk,
    Mixed,
}

/// <summary>
/// One sender's live-message counts, as <see cref="SenderStatsRebuildJob"/> computes them. <see cref="BulkOrListUnsubscribe"/>
/// counts each message with a bulk header, <c>List-Unsubscribe</c> or both once; it is not stored.
/// </summary>
public readonly record struct SenderCounts(
    int Total,
    int Unread = 0,
    int Replied = 0,
    int Starred = 0,
    int ListUnsubscribe = 0,
    int BulkHeader = 0,
    int Primary = 0,
    int Promotions = 0,
    int Social = 0,
    int Updates = 0,
    int Forums = 0,
    int BulkOrListUnsubscribe = 0);

/// <summary>The Stage-0 sender classification (DESIGN §6.4): pure, no LLM. Ratios are compared in integers, so the boundaries are exact.</summary>
public static class SenderStatsCalculator
{
    /// <summary>
    /// <see cref="SenderKind.Human"/> when the user replied, or when nothing about the sender is bulk, at least two
    /// messages arrived and fewer than half are unread; else <see cref="SenderKind.Bulk"/> when at least 80 % carry bulk
    /// or list-unsubscribe headers, or sit in Promotions or Social; else <see cref="SenderKind.Mixed"/> when at least ten
    /// messages split at least 20 % Promotions/Social and 20 % Primary/Updates; else <see cref="SenderKind.Unknown"/>.
    /// </summary>
    public static SenderKind Kind(SenderCounts c)
    {
        var headers = c.BulkOrListUnsubscribe;
        var marketing = c.Promotions + c.Social;
        var personal = c.Primary + c.Updates;
        if (c.Replied > 0 || (headers + marketing == 0 && c.Total >= 2 && 2 * c.Unread < c.Total))
        {
            return SenderKind.Human;
        }

        if (c.Total > 0 && (5 * headers >= 4 * c.Total || 5 * marketing >= 4 * c.Total))
        {
            return SenderKind.Bulk;
        }

        return c.Total >= 10 && 5 * marketing >= c.Total && 5 * personal >= c.Total ? SenderKind.Mixed : SenderKind.Unknown;
    }
}
