using System.ComponentModel;
using System.Diagnostics;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>
/// The harness <see cref="ImageAttachmentTests"/> and <see cref="TesseractOcrEngineTests"/> share: one synthetic message
/// with the given attachments, converted by the PDF and image converters over a stubbed or real OCR engine and the fake
/// vision client.
/// </summary>
internal static class ImageTestSupport
{
    public const string MessageId = "msg-img-0001";
    public const string BaseUrl = "http://ollama.example.com:11434";
    public static readonly ImageReading Ocr = new(ImageMode.Ocr, null, BaseUrl);
    public static readonly ImageReading Vision = new(ImageMode.Vision, "vision-model-a", BaseUrl);
    public static readonly IReadOnlySet<AttachmentType> ImagesAndPdfs = new HashSet<AttachmentType> { AttachmentType.Image, AttachmentType.Pdf };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static byte[] Png => SyntheticImage.Png;

    public static readonly Lazy<bool> TesseractInstalled = new(() =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("tesseract", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    });

    public static ConversionLimits Limits(ImageReading? images) => new AttachmentSettings().ToLimits() with { Images = images };

    public static TesseractOcrEngine Tesseract(string path = "tesseract", int memoryLimitMb = AttachmentOptions.DefaultOcrMemoryLimitMb) =>
        new(Options.Create(new AttachmentOptions { TesseractPath = path, OcrMemoryLimitMb = memoryLimitMb }), NullLogger<TesseractOcrEngine>.Instance);

    public static ImageTextReader Reader(IOcrEngine ocr, FakeLlmClientFactory? llm = null, TimeSpan? timeout = null) =>
        new(ocr, new OllamaVisionClient(llm ?? new FakeLlmClientFactory()),
            Options.Create(new AttachmentOptions { ImageTimeout = timeout ?? TimeSpan.FromSeconds(30) }), Options.Create(new LlmOptions()));

    public static async Task<ConvertedAttachment> ConvertImageAsync(
        byte[] image, ConversionLimits limits, IOcrEngine ocr, FakeLlmClientFactory? llm = null)
    {
        using var stream = new MemoryStream(image);
        return await new ImageAttachmentConverter(Reader(ocr, llm))
            .ConvertAsync(new GmailAttachment("att-1", "scan.png", "image/png", image.Length), stream, limits, Ct);
    }

    public static async Task<AttachmentDigest> ConvertAllAsync(
        (string Filename, string MimeType, byte[] Content)[] attachments, ConversionLimits limits, IOcrEngine ocr,
        FakeLlmClientFactory? llm = null, TimeSpan? timeout = null) =>
        await ConvertAllAsync(Message(attachments), ImagesAndPdfs, limits, ocr, llm, timeout);

    public static async Task<AttachmentDigest> ConvertAllAsync(
        IGmailClient gmail, IReadOnlySet<AttachmentType> enabled, ConversionLimits limits, IOcrEngine ocr,
        FakeLlmClientFactory? llm = null, TimeSpan? timeout = null)
    {
        var reader = Reader(ocr, llm, timeout);
        var service = new AttachmentConversionService(
            gmail,
            [new PdfAttachmentConverter(scanReader: reader, maxOcrPages: 3), new ImageAttachmentConverter(reader)],
            NullLogger<AttachmentConversionService>.Instance);
        var attachments = (await gmail.GetMessageContentAsync(MessageId, Ct)).ShouldNotBeNull().Attachments;
        return await service.ConvertAllAsync(MessageId, [.. attachments], enabled, limits, Ct);
    }

    public static FakeGmailClient Message(params (string Filename, string MimeType, byte[] Content)[] attachments) =>
        new(new FakeTokenStore(TimeProvider.System), [new FakeMessage(
            MessageId, "thread-1", "Sender <sender@example.com>", "Synthetic subject", DateTimeOffset.UnixEpoch, ["INBOX"],
            Attachments: [.. attachments.Select((a, i) => new FakeAttachment($"att-{i + 1}", a.Filename, a.MimeType, a.Content.Length, a.Content))])]);

    public static (string, string, byte[]) Att(string filename, string mimeType, byte[] content) => (filename, mimeType, content);

    /// <summary>
    /// Records each image and answers with <paramref name="answer"/>, or never answers when <paramref name="hang"/> is set;
    /// not installed when <paramref name="available"/> is false.
    /// </summary>
    public sealed class StubOcr(Func<byte[], string>? answer = null, bool hang = false, bool available = true) : IOcrEngine
    {
        public List<byte[]> Images { get; } = [];

        public void EnsureAvailable()
        {
            if (!available)
            {
                throw new OcrUnavailableException("The stub OCR engine is not installed.");
            }
        }

        public async Task<string> ReadTextAsync(ReadOnlyMemory<byte> image, CancellationToken ct)
        {
            var bytes = image.ToArray();
            Images.Add(bytes);
            if (hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return answer?.Invoke(bytes) ?? "";
        }
    }
}
