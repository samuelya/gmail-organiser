using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// Converts a message's enabled, supported attachments within <see cref="ConversionLimits"/>. The caller passes the list
/// from <see cref="IGmailClient.GetMessageContentAsync"/>, which also carries the body, so a message costs one
/// <c>messages.get</c>. Size (when Gmail reports it) and the per-message cap are checked before download, so skipped
/// attachments cost no quota. The markdown lives only in the returned digest: never stored, never logged (filenames at
/// Debug only).
/// </summary>
public sealed class AttachmentConversionService(
    IGmailClient gmail,
    IEnumerable<IAttachmentConverter> converters,
    ILogger<AttachmentConversionService> logger)
{
    private readonly IReadOnlyList<IAttachmentConverter> converters = [.. converters];

    /// <remarks>
    /// Any other failure of one attachment's download or conversion, timeouts included, is recorded as
    /// <see cref="SkipReason.Failed"/> and the rest still convert.
    /// </remarks>
    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    public async Task<AttachmentDigest> ConvertAllAsync(
        string messageId,
        IReadOnlyList<GmailAttachment> attachments,
        IReadOnlySet<AttachmentType> enabledTypes,
        ConversionLimits limits,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(enabledTypes);
        ArgumentNullException.ThrowIfNull(limits);
        if (attachments.Count == 0)
        {
            return AttachmentDigest.Empty;
        }

        var converted = new List<ConvertedAttachment>();
        var skipped = new List<SkippedAttachment>();
        var downloads = 0;
        foreach (var attachment in attachments)
        {
            var type = AttachmentTypeResolver.Resolve(attachment.MimeType, attachment.Filename);
            var converter = converters.FirstOrDefault(c => c.CanConvert(type));
            SkipReason? reason =
                !enabledTypes.Contains(type) ? SkipReason.Disabled
                : converter is null ? SkipReason.Unsupported
                : attachment.Size > limits.MaxBytesFor(type) ? SkipReason.TooLarge
                : downloads >= limits.MaxPerMessage ? SkipReason.TooMany
                : null;
            if (reason is null)
            {
                downloads++;
                (var result, reason) = await ConvertAsync(messageId, attachment, type, converter!, limits, ct);
                if (result is not null)
                {
                    converted.Add(result);
                    continue;
                }
            }

            logger.LogDebug("Skipped attachment {Filename} ({Type}): {Reason}", attachment.Filename, type, reason);
            skipped.Add(new SkippedAttachment(attachment.Filename, type, reason!.Value));
        }

        logger.LogInformation(
            "Converted {Converted} of {Total} attachments of a message ({Skipped} skipped)", converted.Count, attachments.Count, skipped.Count);
        return new AttachmentDigest(converted, skipped);
    }

    /// <summary>
    /// Reads (inline bytes or one download) and converts one attachment, or says why not. An unknown size is checked
    /// after the download. Gmail connection and quota errors and cancellation of <paramref name="ct"/> propagate.
    /// </summary>
    private async Task<(ConvertedAttachment? Result, SkipReason? Reason)> ConvertAsync(
        string messageId, GmailAttachment attachment, AttachmentType type, IAttachmentConverter converter, ConversionLimits limits, CancellationToken ct)
    {
        try
        {
            var content = attachment.InlineContent
                ?? (attachment.AttachmentId is { } attachmentId ? await gmail.GetAttachmentContentAsync(messageId, attachmentId, ct) : null);
            if (content is null)
            {
                logger.LogWarning("An attachment was gone or unreadable; skipped");
                return (null, SkipReason.Failed);
            }

            if (content.Length > limits.MaxBytesFor(type))
            {
                logger.LogWarning("An attachment of unknown or understated size was larger than the limit; skipped");
                return (null, SkipReason.TooLarge);
            }

            using var stream = new MemoryStream(content, writable: false);
            var result = await converter.ConvertAsync(attachment, stream, limits, ct);
            var (markdown, truncated) = limits.Truncate(result.Markdown);
            return (result with { AttachmentType = type, Markdown = markdown, Truncated = result.Truncated || truncated }, null);
        }
        catch (Exception ex) when (ex is not (GmailNotConnectedException or GmailRateLimitedException) && !ct.IsCancellationRequested)
        {
            // The exception type only: messages of document parsers and Gmail errors can quote content or IDs.
            logger.LogWarning("Reading or converting an attachment failed with {ExceptionType}", ex.GetType().Name);
            return (null, SkipReason.Failed);
        }
    }
}
