namespace GmailOrganiser.Gmail.Fake;

/// <summary>An attachment in the fake mailbox; <see cref="Content"/> is a generated <see cref="SyntheticPdf"/>.</summary>
public sealed record FakeAttachment(string AttachmentId, string Filename, string MimeType, int Size, byte[] Content);

/// <summary>A message in the fake mailbox (synthetic data only).</summary>
/// <param name="From">The raw <c>From</c> header.</param>
/// <param name="BodyText">The raw <c>text/plain</c> part, if any.</param>
/// <param name="BodyHtml">The raw <c>text/html</c> part, if any.</param>
/// <param name="SizeEstimate">Size of the message without attachments; Gmail's estimate adds the attachments.</param>
public sealed record FakeMessage(
    string Id,
    string ThreadId,
    string From,
    string Subject,
    DateTimeOffset Date,
    IReadOnlyList<string> LabelIds,
    string? To = null,
    string? ListId = null,
    string? ListUnsubscribe = null,
    string? Snippet = null,
    int SizeEstimate = 2048,
    string HistoryId = "1",
    IReadOnlyList<FakeAttachment>? Attachments = null,
    string? BodyText = null,
    string? BodyHtml = null,
    string? ListUnsubscribePost = null,
    string? Precedence = null,
    string? AutoSubmitted = null)
{
    public IReadOnlyList<FakeAttachment> Attachments { get; init; } = Attachments ?? [];

    public bool HasAttachment => Attachments.Count > 0;

    /// <summary>What Gmail reports as <c>sizeEstimate</c>: the message plus its attachments.</summary>
    public int TotalSizeEstimate => SizeEstimate + Attachments.Sum(a => a.Size);

    /// <summary>The top-level MIME type Gmail would report, mirroring real mail.</summary>
    public string MimeType => HasAttachment ? GmailMetadataMapper.AttachmentMimeType : "multipart/alternative";
}
