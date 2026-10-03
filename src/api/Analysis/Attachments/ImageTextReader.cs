using GmailOrganiser.Gmail;
using GmailOrganiser.Llm;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>What one image shows: a one-line description and the text visible in it (empty when there is none).</summary>
public sealed record ImageText(string Description, string Text)
{
    public string ToMarkdown() => Text.Length == 0 ? Description : $"{Description}\n\n{Text}";
}

/// <summary>Local OCR: <see cref="TesseractOcrEngine"/>, or a stub in tests.</summary>
public interface IOcrEngine
{
    /// <summary>Cheap check that the engine is installed, so its absence is reported before an image is looked at.</summary>
    /// <exception cref="OcrUnavailableException">The engine is not installed.</exception>
    void EnsureAvailable();

    /// <returns>The recognised text; empty when there is none.</returns>
    /// <exception cref="OcrUnavailableException">The engine is not installed or can't be started.</exception>
    Task<string> ReadTextAsync(ReadOnlyMemory<byte> image, CancellationToken ct);
}

/// <summary>No OCR engine to run: every image would fail the same way, so a scanned PDF stays unread instead.</summary>
public sealed class OcrUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A local Ollama vision model: <see cref="OllamaVisionClient"/>, or a fake in tests.</summary>
public interface IVisionClient
{
    /// <param name="reading">An <see cref="ImageMode.Vision"/> reading with its model.</param>
    /// <exception cref="InvalidDataException">The image format is unknown, or the answer is not the expected JSON.</exception>
    Task<ImageText> ReadAsync(ReadOnlyMemory<byte> image, ImageReading reading, CancellationToken ct);
}

/// <summary>
/// Reads one image (an attachment or a scanned PDF page) by OCR or the vision model, as <see cref="ImageReading"/>
/// says, within <see cref="AttachmentOptions.ImageTimeoutFor"/>. A PNG, JPEG or WebP whose header declares more than
/// <see cref="ImageHeader.MaxPixels"/> is not decoded; that check is the only bound for the vision path (Ollama decodes
/// on the host) and a cheap pre-filter for OCR, whose process is bounded by <see cref="AttachmentOptions.OcrMemoryLimitMb"/>
/// for every format. The vision model gets PNG, JPEG and WebP only, the formats Ollama decodes; OCR gets every format
/// <see cref="MediaType"/> knows. The text is never stored or logged.
/// </summary>
public sealed class ImageTextReader(
    IOcrEngine ocr, IVisionClient vision, IOptions<AttachmentOptions> options, IOptions<LlmOptions> llmOptions)
{
    public const string OcrDescription = "Image; text read by OCR, no visual description.";
    public const string OcrNoTextDescription = "Image; OCR found no text in it.";

    /// <exception cref="AttachmentSkippedException">
    /// <see cref="SkipReason.Unsupported"/>: a format the mode's reader doesn't decode (checked first);
    /// <see cref="SkipReason.TooLarge"/>: the header declares more than <see cref="ImageHeader.MaxPixels"/>.
    /// </exception>
    /// <exception cref="OcrUnavailableException">OCR mode without an OCR engine, whatever the image; checked before the header.</exception>
    /// <exception cref="InvalidDataException">The header of a format <see cref="ImageHeader.Reads"/> can't be read, so the image is never decoded.</exception>
    /// <exception cref="TimeoutException">Reading took longer than <see cref="AttachmentOptions.ImageTimeoutFor"/>.</exception>
    public async Task<ImageText> ReadAsync(ReadOnlyMemory<byte> image, ImageReading reading, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var mediaType = MediaType(image.Span);
        if (reading.Mode == ImageMode.Vision ? !IsVisionFormat(mediaType) : mediaType is null)
        {
            throw new AttachmentSkippedException(
                SkipReason.Unsupported,
                reading.Mode == ImageMode.Vision ? "The vision model reads PNG, JPEG and WebP only." : "The OCR engine reads PNG, JPEG, GIF, WebP, TIFF, BMP and JPEG 2000 only.");
        }

        // Without an engine every image fails alike; a scanned PDF then stays unread rather than failed (the converter's contract).
        if (reading.Mode == ImageMode.Ocr)
        {
            ocr.EnsureAvailable();
        }

        // Decoders allocate the declared canvas before rejecting anything: a 1 MB PNG made Tesseract take 4 GB (#156).
        if (ImageHeader.Reads(mediaType))
        {
            var pixels = ImageHeader.DeclaredPixels(mediaType, image.Span) ?? throw new InvalidDataException("The image header can't be read.");
            if (pixels > ImageHeader.MaxPixels)
            {
                throw new AttachmentSkippedException(SkipReason.TooLarge, $"The image declares {pixels} pixels; at most {ImageHeader.MaxPixels} are read.");
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.ImageTimeoutFor(reading.Mode, llmOptions.Value.ModelTimeout));
        try
        {
            if (reading.Mode == ImageMode.Vision)
            {
                return await vision.ReadAsync(image, reading, timeout.Token).WaitAsync(timeout.Token);
            }

            var text = (await ocr.ReadTextAsync(image, timeout.Token).WaitAsync(timeout.Token)).Trim();
            return new ImageText(text.Length == 0 ? OcrNoTextDescription : OcrDescription, text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Reading the image took longer than the image timeout.");
        }
    }

    /// <summary>The formats Ollama's image decoder reads.</summary>
    public static bool IsVisionFormat(string? mediaType) => mediaType is "image/png" or "image/jpeg" or "image/webp";

    /// <summary>The media type from the leading bytes, or <c>null</c> for a format the readers don't know.</summary>
    public static string? MediaType(ReadOnlySpan<byte> image) => image switch
    {
        [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x47, 0x49, 0x46, 0x38, ..] => "image/gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..] => "image/tiff",
        [0x42, 0x4D, ..] => "image/bmp",
        [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, ..] or [0xFF, 0x4F, 0xFF, 0x51, ..] => "image/jp2",
        _ => null,
    };
}

/// <summary>An <see cref="AttachmentType.Image"/> as markdown: a one-line description, then the visible text.</summary>
public sealed class ImageAttachmentConverter(ImageTextReader reader) : IAttachmentConverter
{
    public bool CanConvert(AttachmentType type) => type == AttachmentType.Image;

    /// <exception cref="AttachmentSkippedException">
    /// <see cref="SkipReason.Disabled"/> when <see cref="ConversionLimits.Images"/> is <c>null</c> (images are not read);
    /// <see cref="SkipReason.Unsupported"/> for a format the mode's reader doesn't decode; <see cref="SkipReason.TooLarge"/>
    /// when a PNG, JPEG or WebP header declares more than <see cref="ImageHeader.MaxPixels"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">A PNG, JPEG or WebP header can't be read; the image is never decoded (a failed attachment).</exception>
    /// <exception cref="OcrUnavailableException">OCR mode without an OCR engine (a failed attachment).</exception>
    /// <exception cref="TimeoutException">Reading took longer than the image timeout (a failed attachment).</exception>
    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        var reading = limits.Images ?? throw new AttachmentSkippedException(SkipReason.Disabled, "Image attachments are not read with these limits.");

        var text = await reader.ReadAsync(await ReadAllAsync(content, ct), reading, ct);
        return new ConvertedAttachment(attachment.Filename, AttachmentType.Image, text.ToMarkdown(), false);
    }

    /// <summary>The stream's buffer when it exposes one (the conversion service's), else one copy sized up front.</summary>
    private static async Task<ReadOnlyMemory<byte>> ReadAllAsync(Stream content, CancellationToken ct)
    {
        if (content is MemoryStream memory && memory.TryGetBuffer(out var buffer))
        {
            return buffer.AsMemory((int)memory.Position);
        }

        using var copy = content.CanSeek ? new MemoryStream((int)Math.Max(0, content.Length - content.Position)) : new MemoryStream();
        await content.CopyToAsync(copy, ct);
        return copy.GetBuffer().AsMemory(0, (int)copy.Length);
    }
}
