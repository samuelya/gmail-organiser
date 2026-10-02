using System.Text;
using GmailOrganiser.Gmail;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// The PDF text layer as markdown, one block per page separated by <c>---</c> (PdfPig, Apache-2.0). Reading stops once
/// the text exceeds <see cref="ConversionLimits.MaxChars"/>; the caller truncates. A scanned PDF without a text layer
/// yields empty markdown, not a failure, so OCR can pick it up later. The parse runs on the thread pool and gives up
/// after <see cref="DefaultParseTimeout"/> with a <see cref="TimeoutException"/>, so a hostile PDF can't hold up a job.
/// </summary>
/// <param name="parseTimeout">Defaults to <see cref="DefaultParseTimeout"/>; a test seam.</param>
public sealed class PdfAttachmentConverter(TimeSpan? parseTimeout = null) : IAttachmentConverter
{
    public const string PageBreak = "\n\n---\n\n";

    public static readonly TimeSpan DefaultParseTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan parseTimeout = parseTimeout ?? DefaultParseTimeout;

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Pdf;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(parseTimeout);
        try
        {
            // WaitAsync returns on timeout even while PdfPig is stuck inside one page; the parse sees the token between pages.
            var markdown = await Task.Run(() => ReadText(content, limits.MaxChars, timeout.Token), timeout.Token).WaitAsync(timeout.Token);
            return new ConvertedAttachment(attachment.Filename, AttachmentType.Pdf, markdown, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Reading the PDF took longer than the parse timeout.");
        }
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
