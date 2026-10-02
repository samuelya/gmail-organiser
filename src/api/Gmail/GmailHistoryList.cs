using System.Globalization;
using System.Net;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Util;
using HistoryTypes = Google.Apis.Gmail.v1.UsersResource.HistoryResource.ListRequest.HistoryTypesEnum;

namespace GmailOrganiser.Gmail;

/// <summary>One <c>history.list</c> request with all four history types, mapped to <see cref="HistoryPage"/>.</summary>
public static class GmailHistoryList
{
    /// <summary>Gmail's maximum page size for <c>history.list</c>.</summary>
    public const int MaxPageSize = 500;

    private static readonly HistoryTypes[] AllTypes =
        [HistoryTypes.MessageAdded, HistoryTypes.MessageDeleted, HistoryTypes.LabelAdded, HistoryTypes.LabelRemoved];

    /// <exception cref="ArgumentException"><paramref name="startHistoryId"/> is not a history ID (a data bug, not expiry).</exception>
    /// <exception cref="GmailHistoryExpiredException">Gmail answered 404.</exception>
    /// <exception cref="GmailInvalidPageTokenException">Gmail rejected <paramref name="pageToken"/>.</exception>
    public static async Task<HistoryPage> SendAsync(GmailService service, string startHistoryId, string? pageToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!ulong.TryParse(startHistoryId, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            throw new ArgumentException($"'{startHistoryId}' is not a Gmail history ID.", nameof(startHistoryId));
        }

        var request = service.Users.History.List("me");
        request.StartHistoryId = start;
        request.PageToken = pageToken;
        request.MaxResults = MaxPageSize;
        request.HistoryTypesList = new Repeatable<HistoryTypes>(AllTypes);
        try
        {
            var response = await request.ExecuteAsync(ct);
            return new HistoryPage(
                [.. response.History?.Select(ToRecord) ?? []],
                response.NextPageToken,
                response.HistoryId?.ToString(CultureInfo.InvariantCulture) ?? startHistoryId);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw new GmailHistoryExpiredException("Gmail no longer keeps history from the stored history ID.", ex);
        }
        catch (GoogleApiException ex) when (pageToken is not null && GoogleGmailClient.IsInvalidPageToken(ex))
        {
            throw new GmailInvalidPageTokenException("Gmail rejected the history page token.", ex);
        }
    }

    private static HistoryRecord ToRecord(History h) => new(
        h.Id?.ToString(CultureInfo.InvariantCulture) ?? "",
        [.. h.MessagesAdded?.Select(m => m.Message?.Id).OfType<string>() ?? []],
        [.. h.MessagesDeleted?.Select(m => m.Message?.Id).OfType<string>() ?? []],
        [.. h.LabelsAdded?.Where(l => l.Message?.Id is not null).Select(l => new LabelChange(l.Message.Id, [.. l.LabelIds ?? []])) ?? []],
        [.. h.LabelsRemoved?.Where(l => l.Message?.Id is not null).Select(l => new LabelChange(l.Message.Id, [.. l.LabelIds ?? []])) ?? []]);
}
