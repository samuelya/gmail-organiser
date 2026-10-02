using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>Turns one attachment's bytes into markdown for the prompt. The text is never stored or logged.</summary>
public interface IAttachmentConverter
{
    bool CanConvert(AttachmentType type);

    /// <param name="content">The decoded attachment, at most <see cref="ConversionLimits.MaxBytes"/> long.</param>
    /// <remarks>Throws on content it cannot read; the caller records that as <see cref="SkipReason.Failed"/>.</remarks>
    Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct);
}

/// <param name="MaxBytes">Larger attachments are skipped before download.</param>
/// <param name="MaxChars">Markdown per attachment is cut to this many characters and marked <see cref="TruncatedMarker"/>.</param>
/// <param name="MaxPerMessage">At most this many attachments per message are downloaded, in attachment order.</param>
public sealed record ConversionLimits(long MaxBytes, int MaxChars, int MaxPerMessage)
{
    public const string TruncatedMarker = "[truncated]";

    /// <summary>Code defaults until the settings exist: 5 MB, 4 000 characters, 5 per message.</summary>
    public static ConversionLimits Default { get; } = new(5 * 1024 * 1024, 4000, 5);

    /// <summary><paramref name="markdown"/> cut to <see cref="MaxChars"/> plus the marker, or unchanged when it fits.</summary>
    public (string Markdown, bool Truncated) Truncate(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return markdown.Length <= MaxChars ? (markdown, false) : (markdown[..MaxChars].TrimEnd() + "\n" + TruncatedMarker, true);
    }
}

public sealed record ConvertedAttachment(string Filename, AttachmentType AttachmentType, string Markdown, bool Truncated);

public enum SkipReason
{
    /// <summary>The type is not enabled in settings.</summary>
    Disabled,

    /// <summary>Larger than <see cref="ConversionLimits.MaxBytes"/>; never downloaded.</summary>
    TooLarge,

    /// <summary>Enabled, but no converter handles the type.</summary>
    Unsupported,

    /// <summary>The download or the converter failed.</summary>
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
