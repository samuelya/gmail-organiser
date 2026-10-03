using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>Turns one attachment's bytes into markdown for the prompt. The text is never stored or logged.</summary>
public interface IAttachmentConverter
{
    bool CanConvert(AttachmentType type);

    /// <param name="content">The decoded attachment, within <see cref="ConversionLimits.MaxBytesFor"/> for its type.</param>
    /// <remarks>
    /// Throws on content it cannot read; the caller records that as <see cref="SkipReason.Failed"/>, or as the reason of an
    /// <see cref="AttachmentSkippedException"/>. May stop reading
    /// once the text exceeds <see cref="ConversionLimits.MaxChars"/>; the caller truncates, once.
    /// </remarks>
    Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct);
}

/// <summary>From the settings via <see cref="IAttachmentPolicy"/>; converters never read settings themselves.</summary>
/// <param name="MaxBytes">Larger attachments are skipped before download.</param>
/// <param name="MaxImageBytes"><see cref="MaxBytes"/> for <see cref="AttachmentType.Image"/>.</param>
/// <param name="MaxChars">Markdown per attachment is cut to at most this many characters, <see cref="TruncatedMarker"/> included.</param>
/// <param name="MaxPerMessage">At most this many attachments per message are downloaded, in attachment order.</param>
/// <param name="Images">How images and scanned PDF pages are read; <c>null</c> when they are not read at all.</param>
public sealed record ConversionLimits(long MaxBytes, long MaxImageBytes, int MaxChars, int MaxPerMessage, ImageReading? Images = null)
{
    public const string TruncatedMarker = "[truncated]";

    /// <summary>The size limit for an attachment of <paramref name="type"/>.</summary>
    public long MaxBytesFor(AttachmentType type) => type == AttachmentType.Image ? MaxImageBytes : MaxBytes;

    private const string TruncatedSuffix = "\n" + TruncatedMarker;

    /// <summary>
    /// <paramref name="markdown"/> unchanged when it fits <see cref="MaxChars"/>; otherwise cut so that with the marker
    /// it is at most <see cref="MaxChars"/> long (the marker alone when the limit is shorter than the marker). Never
    /// splits a surrogate pair, and truncating the result again changes nothing.
    /// </summary>
    public (string Markdown, bool Truncated) Truncate(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (markdown.Length <= MaxChars)
        {
            return (markdown, false);
        }

        var keep = Math.Max(0, MaxChars - TruncatedSuffix.Length);
        if (keep > 0 && char.IsHighSurrogate(markdown[keep - 1]))
        {
            keep--;
        }

        var head = markdown[..keep].TrimEnd();
        return (head.Length == 0 ? TruncatedMarker : head + TruncatedSuffix, true);
    }
}

/// <param name="VisionModel">The Ollama model for <see cref="ImageMode.Vision"/>; never <c>null</c> in that mode.</param>
/// <param name="OllamaBaseUrl">The Ollama server for <see cref="ImageMode.Vision"/>.</param>
public sealed record ImageReading(ImageMode Mode, string? VisionModel, string OllamaBaseUrl);

/// <summary>A converter declines an attachment for <see cref="Reason"/>; the caller records it as skipped, not failed.</summary>
public sealed class AttachmentSkippedException(SkipReason reason, string message) : Exception(message)
{
    public SkipReason Reason { get; } = reason;
}

public sealed record ConvertedAttachment(string Filename, AttachmentType AttachmentType, string Markdown, bool Truncated);

public enum SkipReason
{
    /// <summary>The type is not enabled in settings (images also in vision mode without a vision model).</summary>
    Disabled,

    /// <summary>Larger than <see cref="ConversionLimits.MaxBytesFor"/> for its type; not downloaded unless Gmail didn't report the size.</summary>
    TooLarge,

    /// <summary>Enabled, but no converter handles the type, or its format in the current image mode.</summary>
    Unsupported,

    /// <summary>The download or the converter failed or timed out, or the attachment was gone.</summary>
    Failed,

    /// <summary>Beyond <see cref="ConversionLimits.MaxPerMessage"/>; never downloaded.</summary>
    TooMany,
}

public sealed record SkippedAttachment(string Filename, AttachmentType AttachmentType, SkipReason SkipReason);

/// <summary>What the prompt gets for one message: converted attachments, and the skipped ones by name and type only.</summary>
public sealed record AttachmentDigest(IReadOnlyList<ConvertedAttachment> Converted, IReadOnlyList<SkippedAttachment> Skipped)
{
    public static AttachmentDigest Empty { get; } = new([], []);
}
