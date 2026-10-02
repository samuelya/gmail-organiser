namespace GmailOrganiser.Gmail.Fake;

public sealed partial class FakeGmailClient
{
    public async Task<GmailMessageContent?> GetMessageContentAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync(() =>
            messages.Find(m => m.Id == id) is { } m
                ? new GmailMessageContent(
                    new GmailMessageBody(m.BodyText, m.BodyHtml),
                    [.. m.Attachments.Select(a => new GmailAttachment(a.AttachmentId, a.Filename, a.MimeType, a.Size))])
                : null, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetAttachmentContentAsync(string messageId, string attachmentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync(() =>
            messages.Find(m => m.Id == messageId)?.Attachments.FirstOrDefault(a => a.AttachmentId == attachmentId) is { } a
                ? a.Content.ToArray()
                : null, ct).ConfigureAwait(false);
    }
}
