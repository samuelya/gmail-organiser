using System.Text;
using GmailOrganiser.Gmail;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// The PDF text layer as markdown, one block per page separated by <c>---</c> (PdfPig, Apache-2.0). Reading stops once
/// the text exceeds <see cref="ConversionLimits.MaxChars"/>; the caller truncates. A scanned PDF
/// without a text layer is read as images when <see cref="ConversionLimits.Images"/> is set: the largest embedded image of
/// each of the first <c>maxOcrPages</c> pages goes through <paramref name="scanReader"/> (JPEG as stored, JPEG 2000 as
/// stored for OCR, other encodings via PdfPig's PNG export; CCITT/JBIG2 scans PdfPig can't decode stay unread). An image
/// over <see cref="MaxScanPixels"/> is never decoded, and a decoded one over <see cref="ConversionLimits.MaxImageBytes"/>
/// is not read. A page that can't be read is noted and the others are kept; without an OCR engine, or with no page
/// image at all, it yields empty markdown, not a failure. The parse runs under <see cref="ParseTimeout"/>, so a hostile
/// PDF can't hold up a job; each page image then has its own image timeout.
/// </summary>
/// <param name="parseTimeout">Defaults to 30 s; a test seam.</param>
/// <param name="scanReader"><c>null</c>: scanned PDFs are never read as images.</param>
public sealed class PdfAttachmentConverter(
    TimeSpan? parseTimeout = null, ImageTextReader? scanReader = null, int maxOcrPages = 3, ILogger<PdfAttachmentConverter>? logger = null)
    : IAttachmentConverter
{
    public const string PageBreak = "\n\n---\n\n";

    /// <summary>Declared width × height of a page image above which it is not decoded (an A4 page at 600 dpi is 35 MP).</summary>
    public const long MaxScanPixels = 40_000_000;

    private readonly TimeSpan parseTimeout = parseTimeout ?? ParseTimeout.Default;

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Pdf;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var scanPages = scanReader is not null && limits.Images is not null ? Math.Max(0, maxOcrPages) : 0;
        var (markdown, scans) = await ParseTimeout.RunAsync(t => ReadText(content, limits, scanPages, t), parseTimeout, "PDF", ct);
        if (scans.Count > 0)
        {
            markdown = await ReadScansAsync(scans, limits, ct);
        }

        return new ConvertedAttachment(attachment.Filename, AttachmentType.Pdf, markdown, false);
    }

    /// <summary>A page's image to read; <see cref="Image"/> is <c>null</c> when it can't be decoded or is too large.</summary>
    private sealed record PageScan(int Page, byte[]? Image);

    /// <summary>
    /// The page images' text, page by page, after a line naming the pages read; an unreadable page is noted in its place.
    /// Empty when there is no OCR engine.
    /// </summary>
    private async Task<string> ReadScansAsync(IReadOnlyList<PageScan> scans, ConversionLimits limits, CancellationToken ct)
    {
        var reading = limits.Images!;
        var how = reading.Mode == ImageMode.Vision ? "the vision model" : "OCR";
        var body = new StringBuilder();
        var read = new List<int>();
        var found = false;
        var stopped = false;
        foreach (var scan in scans)
        {
            if (body.Length > limits.MaxChars)
            {
                stopped = true;
                break;
            }

            string? text = null;
            if (scan.Image is not null)
            {
                try
                {
                    text = (await scanReader!.ReadAsync(scan.Image, reading, ct)).Text;
                    read.Add(scan.Page);
                    found |= text.Length > 0;
                }
                catch (OcrUnavailableException)
                {
                    return "";
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // The exception type only: messages can quote content.
                    logger?.LogWarning("Reading page {Page} of a scanned PDF failed with {ExceptionType}", scan.Page, ex.GetType().Name);
                }
            }

            var block = text ?? $"[page {scan.Page} unreadable]";
            if (block.Length > 0)
            {
                body.Append(body.Length > 0 ? PageBreak : "").Append(block);
            }
        }

        var header = read.Count == 0
            ? "Scanned PDF without a text layer; no page could be read."
            : $"Scanned PDF without a text layer; page(s) {string.Join(", ", read)} read by {how}"
                + (stopped ? $", stopped after page {read[^1]} at the character limit" : "")
                + (found ? "." : "; no text found.");
        return body.Length == 0 ? header : $"{header}\n\n{body}";
    }

    /// <summary>
    /// The text layer; while it is still empty, also the largest image of each of the first <paramref name="scanPages"/>
    /// pages, decoded only when the whole text layer turns out empty.
    /// </summary>
    private static (string Markdown, IReadOnlyList<PageScan> Scans) ReadText(
        Stream content, ConversionLimits limits, int scanPages, CancellationToken ct)
    {
        var markdown = new StringBuilder();
        var images = new List<(int Page, IPdfImage Image)>();
        using var document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            if (markdown.Length == 0 && page.Number <= scanPages
                && page.GetImages().Where(i => !i.IsImageMask).MaxBy(i => (long)i.WidthInSamples * i.HeightInSamples) is { } image)
            {
                images.Add((page.Number, image));
            }

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

        if (markdown.Length > 0 || images.Count == 0)
        {
            return (markdown.ToString(), []);
        }

        var vision = limits.Images?.Mode == ImageMode.Vision;
        return ("", [.. images.Select(i =>
        {
            ct.ThrowIfCancellationRequested();
            return new PageScan(i.Page, ScanBytes(i.Image, vision, limits.MaxImageBytes));
        })]);
    }

    /// <summary>
    /// JPEG (and JPEG 2000 for OCR) as stored; anything else PdfPig can decode becomes PNG. <c>null</c> when it can't be
    /// decoded, its declared size is over <see cref="MaxScanPixels"/> (checked before decoding), or the result is over
    /// <paramref name="maxBytes"/>.
    /// </summary>
    private static byte[]? ScanBytes(IPdfImage image, bool vision, long maxBytes)
    {
        if ((long)image.WidthInSamples * image.HeightInSamples is <= 0 or > MaxScanPixels)
        {
            return null;
        }

        var raw = image.RawMemory.Span;
        var bytes = ImageTextReader.MediaType(raw) is "image/jpeg" || (!vision && ImageTextReader.MediaType(raw) is "image/jp2")
            ? raw.ToArray()
            : image.TryGetPng(out var png) ? png : null;
        return bytes is not null && bytes.Length <= maxBytes ? bytes : null;
    }
}
