namespace GmailOrganiser.Gmail.Fake;

public sealed partial class FakeGmailClient
{
    public async Task<IReadOnlyList<GmailAttachment>> GetAttachmentsAsync(string messageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RetryAsync<IReadOnlyList<GmailAttachment>>(() =>
            messages.Find(m => m.Id == messageId) is { } m
                ? [.. m.Attachments.Select(a => new GmailAttachment(a.AttachmentId, a.Filename, a.MimeType, a.Size))]
                : [], ct).ConfigureAwait(false);
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
