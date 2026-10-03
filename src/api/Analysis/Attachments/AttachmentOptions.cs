using System.ComponentModel.DataAnnotations;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>Converter and image-reading settings that are not user-facing, bound from the <c>Attachments</c> section.</summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";
    public const int DefaultMaxSheetRows = 200;

    /// <summary>Rows read per worksheet or CSV file, the header row included.</summary>
    [Range(1, 10_000)]
    public int MaxSheetRows { get; set; } = DefaultMaxSheetRows;

    public static readonly TimeSpan DefaultOcrTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Per image or scanned PDF page. Unset: <see cref="DefaultOcrTimeout"/> for OCR, and for the vision model
    /// <c>Llm:ModelTimeoutSeconds</c>, which allows for a cold model load.
    /// </summary>
    public TimeSpan? ImageTimeout { get; set; }

    /// <summary>Pages of a PDF without a text layer that are read as images.</summary>
    [Range(0, 100)]
    public int MaxOcrPages { get; set; } = 3;

    /// <summary>The Tesseract executable: a name on <c>PATH</c> or a full path.</summary>
    public string TesseractPath { get; set; } = "tesseract";

    /// <summary>Tesseract languages, e.g. <c>eng+deu</c>; each needs its traineddata installed (the image has <c>eng</c>).</summary>
    public string OcrLanguages { get; set; } = "eng";

    public const int DefaultOcrMemoryLimitMb = 1024;

    /// <summary>
    /// Address-space limit (MiB) for each Tesseract process, applied with <c>prlimit</c> on Linux: an image whose decoder
    /// asks for more fails on its own, whatever its header said. An A4 page at 600 dpi needs about half the default.
    /// </summary>
    [Range(256, 65_536)]
    public int OcrMemoryLimitMb { get; set; } = DefaultOcrMemoryLimitMb;

    /// <param name="modelTimeout"><see cref="Llm.LlmOptions.ModelTimeout"/>.</param>
    public TimeSpan ImageTimeoutFor(ImageMode mode, TimeSpan modelTimeout) =>
        ImageTimeout is { } set && set > TimeSpan.Zero ? set
        : mode == ImageMode.Vision ? modelTimeout
        : DefaultOcrTimeout;
}
