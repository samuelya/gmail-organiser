using System.Text;
using GmailOrganiser.Gmail;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// The PDF text layer as markdown, one block per page separated by <c>---</c> (PdfPig, Apache-2.0). Reading stops once
/// the text exceeds <see cref="ConversionLimits.MaxChars"/>; the caller truncates. A scanned PDF without a text layer
/// yields empty markdown, not a failure, so OCR can pick it up later. The parse runs under <see cref="ParseTimeout"/>,
/// so a hostile PDF can't hold up a job.
/// </summary>
/// <param name="parseTimeout">Defaults to 30 s; a test seam.</param>
public sealed class PdfAttachmentConverter(TimeSpan? parseTimeout = null) : IAttachmentConverter
{
    public const string PageBreak = "\n\n---\n\n";

    private readonly TimeSpan parseTimeout = parseTimeout ?? ParseTimeout.Default;

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Pdf;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var markdown = await ParseTimeout.RunAsync(t => ReadText(content, limits.MaxChars, t), parseTimeout, "PDF", ct);
        return new ConvertedAttachment(attachment.Filename, AttachmentType.Pdf, markdown, false);
    }

    private static string ReadText(Stream content, int maxChars, CancellationToken ct)
    {
        var markdown = new StringBuilder();
        using var document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (markdown.Length > 0)
            {
                markdown.Append(PageBreak);
            }

            markdown.Append(text);
            if (markdown.Length > maxChars)
            {
                break;
            }
        }

        return markdown.ToString();
    }
}
