using System.Buffers.Text;
using System.Net;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;

namespace GmailOrganiser.Gmail;

public sealed partial class GoogleGmailClient
{
    public Task<GmailMessageContent?> GetMessageContentAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.MessageCallUnits, token);
            var request = service.Users.Messages.Get(Me, id);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            try
            {
                var message = await request.ExecuteAsync(token);
                var content = new GmailMessageContent(ReadBody(message), ReadAttachments(message));
                logger.LogDebug("Read a Gmail message with {Count} attachments", content.Attachments.Count);
                return content;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Gmail no longer has the message whose content was requested");
                return null;
            }
        }, ct), ct);
    }

    /// <summary>
    /// Named leaf parts, depth-first through <c>payload.parts</c> in MIME order: by <c>body.attachmentId</c>, or with
    /// their inline <c>body.data</c> decoded when Gmail sent no id (neither when the data doesn't decode). A named
    /// multipart is descended into. An attached message (<c>message/rfc822</c>) is never descended into, as in
    /// <see cref="ReadBody"/>: listed once when named, skipped when not, so its inner files never count as this message's.
    /// </summary>
    public static IReadOnlyList<GmailAttachment> ReadAttachments(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var attachments = new List<GmailAttachment>();
        var stack = new Stack<MessagePart>();
        if (message.Payload is not null)
        {
            stack.Push(message.Payload);
        }

        while (stack.Count > 0)
        {
            var part = stack.Pop();
            var isMessage = IsMimeType(part, "message/rfc822");
            if (!string.IsNullOrEmpty(part.Filename) && (isMessage || part.Parts is not { Count: > 0 }))
            {
                attachments.Add(ToAttachment(part, part.Filename));
                continue;
            }

            if (!isMessage && part.Parts is { Count: > 0 } children)
            {
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
        }

        return attachments;
    }

    private static GmailAttachment ToAttachment(MessagePart part, string filename)
    {
        var mimeType = part.MimeType ?? "";
        if (part.Body?.AttachmentId is { Length: > 0 } attachmentId)
        {
            return new GmailAttachment(attachmentId, filename, mimeType, part.Body.Size);
        }

        byte[]? inline;
        try
        {
            inline = part.Body?.Data is { } data ? Base64Url.DecodeFromChars(data) : [];
        }
        catch (FormatException)
        {
            inline = null;
        }

        return new GmailAttachment(null, filename, mimeType, part.Body?.Size ?? inline?.Length, inline);
    }

    public Task<byte[]?> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        return RunAsync(service => retry.ExecuteAsync(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.MessageCallUnits, token);
            try
            {
                var body = await service.Users.Messages.Attachments.Get(Me, messageId, attachmentId).ExecuteAsync(token);
                var content = body.Data is { } data ? Base64Url.DecodeFromChars(data) : [];
                logger.LogDebug("Read a Gmail attachment of {Bytes} bytes", content.Length);
                return content;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Gmail no longer has the requested attachment");
                return null;
            }
        }, ct), ct);
    }
}
