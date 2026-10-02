using System.Text;
using GmailOrganiser.Gmail;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// The PDF text layer as markdown, one block per page separated by <c>---</c> (PdfPig, Apache-2.0). Reading stops once
/// the text exceeds <see cref="ConversionLimits.MaxChars"/>. A scanned PDF without a text layer yields empty markdown,
/// not a failure, so OCR can pick it up later.
/// </summary>
public sealed class PdfAttachmentConverter : IAttachmentConverter
{
    public const string PageBreak = "\n\n---\n\n";

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Pdf;

    public Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var markdown = new StringBuilder();
        using (var document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true }))
        {
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
                if (markdown.Length > limits.MaxChars)
                {
                    break;
                }
            }
        }

        var (truncatedMarkdown, truncated) = limits.Truncate(markdown.ToString());
        return Task.FromResult(new ConvertedAttachment(attachment.Filename, AttachmentType.Pdf, truncatedMarkdown, truncated));
    }
}
