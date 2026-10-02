using System.Globalization;
using Google.Apis.Gmail.v1.Data;

namespace GmailOrganiser.Gmail;

/// <summary>A parsed <c>From</c> header: the address (lower-cased) and the display name, if any.</summary>
public sealed record SenderAddress(string Address, string? DisplayName)
{
    /// <summary>The part after the last <c>@</c>, or empty when the address has none.</summary>
    public string Domain => Address.LastIndexOf('@') is var at and >= 0 ? Address[(at + 1)..] : "";
}

/// <summary>Maps a Gmail <c>format=metadata</c> message to <see cref="GmailMessageMetadata"/>. Pure.</summary>
public static class GmailMetadataMapper
{
    /// <summary>The headers requested with <c>format=metadata</c>.</summary>
    public static readonly IReadOnlyList<string> MetadataHeaders = ["From", "To", "Subject", "Date", "List-Id", "List-Unsubscribe"];

    /// <summary>
    /// <c>HasAttachment</c> heuristic. <c>format=metadata</c> returns the top-level <c>payload.mimeType</c> and headers
    /// but no parts, so a message counts as having attachments when its top-level type is <c>multipart/mixed</c>.
    /// Misses attachments nested only in <c>multipart/related</c>/<c>alternative</c>, and may flag a mixed message whose
    /// parts are all inline. Bulk reads never switch to <c>format=full</c> (bodies are not stored, DESIGN §7).
    /// </summary>
    public const string AttachmentMimeType = "multipart/mixed";

    public static GmailMessageMetadata Map(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var headers = message.Payload?.Headers ?? [];
        string? Header(string name) => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

        return new GmailMessageMetadata(
            Id: message.Id ?? throw new ArgumentException("Gmail message has no id.", nameof(message)),
            ThreadId: message.ThreadId ?? "",
            HistoryId: message.HistoryId?.ToString(CultureInfo.InvariantCulture) ?? "",
            InternalDate: DateTimeOffset.FromUnixTimeMilliseconds(message.InternalDate ?? 0),
            LabelIds: [.. message.LabelIds ?? []],
            From: Header("From") ?? "",
            To: Header("To"),
            Subject: Header("Subject"),
            ListId: ParseListId(Header("List-Id")),
            ListUnsubscribe: Header("List-Unsubscribe"),
            Snippet: message.Snippet,
            SizeEstimate: message.SizeEstimate ?? 0,
            HasAttachment: string.Equals(message.Payload?.MimeType, AttachmentMimeType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Splits <c>Name &lt;addr&gt;</c>, <c>"Name" &lt;addr&gt;</c> or a bare <c>addr</c>.</summary>
    public static SenderAddress ParseFrom(string? from)
    {
        var value = from?.Trim() ?? "";
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        if (open < 0 || close < open)
        {
            return new SenderAddress(value.ToLowerInvariant(), null);
        }

        var address = value[(open + 1)..close].Trim().ToLowerInvariant();
        var name = value[..open].Trim();
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
        {
            name = name[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Trim();
        }

        return new SenderAddress(address, name.Length == 0 ? null : name);
    }

    /// <summary><c>List-Id</c> is <c>Description &lt;list.id&gt;</c> or a bare id; returns the id without brackets.</summary>
    public static string? ParseListId(string? listId)
    {
        if (string.IsNullOrWhiteSpace(listId))
        {
            return null;
        }

        var value = listId.Trim();
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        return open >= 0 && close > open ? value[(open + 1)..close].Trim() : value;
    }
}
