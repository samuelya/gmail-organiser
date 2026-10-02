using System.Buffers.Text;
using System.Net;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;

namespace GmailOrganiser.Gmail;

public sealed partial class GoogleGmailClient
{
    public Task<IReadOnlyList<GmailAttachment>> GetAttachmentsAsync(string messageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return RunAsync(service => retry.ExecuteAsync<IReadOnlyList<GmailAttachment>>(async token =>
        {
            await quota.AcquireAsync(GmailQuotaLimiter.MessageCallUnits, token);
            var request = service.Users.Messages.Get(Me, messageId);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            try
            {
                var attachments = ReadAttachments(await request.ExecuteAsync(token));
                logger.LogDebug("Listed {Count} attachments of a Gmail message", attachments.Count);
                return attachments;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("Gmail no longer has the message whose attachments were requested");
                return [];
            }
        }, ct), ct);
    }

    /// <summary>
    /// Parts with a filename and a <c>body.attachmentId</c>, depth-first through <c>payload.parts</c> in MIME order. An
    /// attachment's own children (an attached message's parts) are not listed separately.
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
            if (!string.IsNullOrEmpty(part.Filename) && part.Body?.AttachmentId is { Length: > 0 } attachmentId)
            {
                attachments.Add(new GmailAttachment(attachmentId, part.Filename, part.MimeType ?? "", part.Body.Size ?? 0));
                continue;
            }

            if (part.Parts is { Count: > 0 } children)
            {
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
        }

        return attachments;
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
