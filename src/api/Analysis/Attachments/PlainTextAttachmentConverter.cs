using System.Text;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// Text and markdown files as they are. UTF-8 unless a byte order mark says otherwise (the BOM is dropped); invalid
/// bytes become U+FFFD rather than a failure. Reads at most one character past <see cref="ConversionLimits.MaxChars"/>;
/// the caller truncates.
/// </summary>
public sealed class PlainTextAttachmentConverter : IAttachmentConverter
{
    public bool CanConvert(AttachmentType type) => type == AttachmentType.PlainText;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var text = await ReadAsync(content, limits.MaxChars, ct);
        return new ConvertedAttachment(attachment.Filename, AttachmentType.PlainText, text.ReplaceLineEndings("\n").Trim(), false);
    }

    /// <summary>At most <paramref name="maxChars"/> + 1 characters of <paramref name="content"/>, BOM-aware.</summary>
    internal static async Task<string> ReadAsync(Stream content, int maxChars, CancellationToken ct)
    {
        using var reader = new StreamReader(content, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var buffer = new char[maxChars + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return new string(buffer, 0, read);
    }
}
