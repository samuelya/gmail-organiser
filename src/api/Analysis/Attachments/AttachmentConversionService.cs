using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// Reads a message's attachments from Gmail and converts the enabled, supported ones within <see cref="ConversionLimits"/>.
/// Size and the per-message cap are checked before download, so skipped attachments cost no quota. The markdown lives
/// only in the returned digest: never stored, never logged (filenames at Debug only).
/// </summary>
public sealed class AttachmentConversionService(
    IGmailClient gmail,
    IEnumerable<IAttachmentConverter> converters,
    ILogger<AttachmentConversionService> logger)
{
    private readonly IReadOnlyList<IAttachmentConverter> converters = [.. converters];

    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    /// <exception cref="GmailRateLimitedException">Gmail kept rate-limiting after the last retry.</exception>
    public async Task<AttachmentDigest> ConvertAllAsync(
        string messageId, IReadOnlySet<AttachmentType> enabledTypes, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(enabledTypes);
        ArgumentNullException.ThrowIfNull(limits);

        var attachments = await gmail.GetAttachmentsAsync(messageId, ct);
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
                : attachment.Size > limits.MaxBytes ? SkipReason.TooLarge
                : downloads >= limits.MaxPerMessage ? SkipReason.TooMany
                : null;
            if (reason is null)
            {
                downloads++;
                if (await ConvertAsync(messageId, attachment, type, converter!, limits, ct) is { } result)
                {
                    converted.Add(result);
                    continue;
                }

                reason = SkipReason.Failed;
            }

            logger.LogDebug("Skipped attachment {Filename} ({Type}): {Reason}", attachment.Filename, type, reason);
            skipped.Add(new SkippedAttachment(attachment.Filename, type, reason.Value));
        }

        logger.LogInformation(
            "Converted {Converted} of {Total} attachments of a message ({Skipped} skipped)", converted.Count, attachments.Count, skipped.Count);
        return new AttachmentDigest(converted, skipped);
    }

    /// <summary>Downloads and converts one attachment; null when either fails. Gmail connection and quota errors propagate.</summary>
    private async Task<ConvertedAttachment?> ConvertAsync(
        string messageId, GmailAttachment attachment, AttachmentType type, IAttachmentConverter converter, ConversionLimits limits, CancellationToken ct)
    {
        var content = await gmail.GetAttachmentContentAsync(messageId, attachment.AttachmentId, ct);
        if (content is null || content.Length > limits.MaxBytes)
        {
            logger.LogWarning("An attachment was gone or larger than its reported size; skipped");
            return null;
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var result = await converter.ConvertAsync(attachment, stream, limits, ct);
            var (markdown, truncated) = limits.Truncate(result.Markdown);
            return result with { AttachmentType = type, Markdown = markdown, Truncated = result.Truncated || truncated };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception type only: messages of document parsers can quote content.
            logger.LogWarning("An attachment converter failed with {ExceptionType}", ex.GetType().Name);
            return null;
        }
    }
}
