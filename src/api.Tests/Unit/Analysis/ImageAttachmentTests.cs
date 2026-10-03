using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>
/// The image converter (stubbed OCR, fake vision client), the policy's image mode, scanned PDFs, and the real Tesseract
/// on rendered synthetic images where it is installed (CI and the api image).
/// </summary>
public sealed class ImageAttachmentTests
{
    private const string MessageId = "msg-img-0001";
    private const string BaseUrl = "http://ollama.example.com:11434";
    private static readonly ImageReading Ocr = new(ImageMode.Ocr, null, BaseUrl);
    private static readonly ImageReading Vision = new(ImageMode.Vision, "vision-model-a", BaseUrl);
    private static readonly IReadOnlySet<AttachmentType> ImagesAndPdfs = new HashSet<AttachmentType> { AttachmentType.Image, AttachmentType.Pdf };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConversionLimits Limits(ImageReading? images) => new AttachmentSettings().ToLimits() with { Images = images };

    [Fact]
    public async Task Ocr_text_follows_a_one_line_description()
    {
        var ocr = new StubOcr(_ => "\n  Total due: 12.00 EUR\nbilling@example.com \n");

        var result = await ConvertImageAsync(Png(), Limits(Ocr), ocr);

        result.ShouldBe(new ConvertedAttachment(
            "scan.png", AttachmentType.Image, $"{ImageTextReader.OcrDescription}\n\nTotal due: 12.00 EUR\nbilling@example.com", false));
        ocr.Images.Single().ShouldBe(Png());
    }

    [Fact]
    public async Task Ocr_without_text_says_so()
    {
        var result = await ConvertImageAsync(Png(), Limits(Ocr), new StubOcr(_ => " \n"));

        result.Markdown.ShouldBe(ImageTextReader.OcrNoTextDescription);
    }

    [Fact]
    public async Task Vision_sends_the_image_and_the_versioned_prompt_to_the_selected_model()
    {
        var llm = new FakeLlmClientFactory(new FakeChatClient("""{"description":"A shop receipt.\nPrinted.","text":" Total 19.98 \n"}"""));
        var result = await ConvertImageAsync(SyntheticImage.Jpeg, Limits(Vision), new StubOcr(_ => throw new InvalidOperationException()), llm);

        result.Markdown.ShouldBe("A shop receipt. Printed.\n\nTotal 19.98");
        llm.Targets.ShouldBe([(new Uri(BaseUrl), "vision-model-a")]);
        var request = llm.Chat.Requests.Single();
        request.Options!.ResponseFormat.ShouldBeOfType<ChatResponseFormatJson>();
        var contents = request.Messages.Single().Contents;
        contents.OfType<TextContent>().Single().Text.ShouldBe(OllamaVisionClient.Prompt);
        contents.OfType<DataContent>().Single().MediaType.ShouldBe("image/jpeg");
        OllamaVisionClient.Prompt.ShouldContain("If no text is visible, say so");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""["description","text"]""")]
    [InlineData("""{"description":"","text":"x"}""")]
    [InlineData("""{"description":"A photo.","text":42}""")]
    [InlineData("""{"description":"A photo."}""")]
    public async Task Vision_answer_outside_the_schema_is_a_failed_attachment(string answer)
    {
        Should.Throw<InvalidDataException>(() => OllamaVisionClient.Parse(answer));
        var llm = new FakeLlmClientFactory(new FakeChatClient(answer));

        var digest = await ConvertAllAsync([Att("photo.png", "image/png", Png())], Limits(Vision), new StubOcr(_ => ""), llm);

        digest.Skipped.ShouldBe([new SkippedAttachment("photo.png", AttachmentType.Image, SkipReason.Failed)]);
    }

    [Fact]
    public async Task Vision_skips_formats_it_cannot_read_as_unsupported_without_calling_the_model()
    {
        var llm = new FakeLlmClientFactory();
        byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0, 0];
        byte[] tiff = [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0, 0, 0];

        var digest = await ConvertAllAsync(
            [Att("photo.heic", "image/heic", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]), Att("a.gif", "image/gif", gif), Att("b.tif", "image/tiff", tiff)],
            Limits(Vision), new StubOcr(_ => ""), llm);

        digest.Skipped.Select(s => s.SkipReason).ShouldBe([SkipReason.Unsupported, SkipReason.Unsupported, SkipReason.Unsupported]);
        llm.Chat.Requests.ShouldBeEmpty();
        ImageTextReader.IsVisionFormat("image/webp").ShouldBeTrue();
    }

    [Fact]
    public async Task Image_converter_without_image_limits_is_a_disabled_skip_not_a_failure()
    {
        var ocr = new StubOcr(_ => "text");

        var digest = await ConvertAllAsync([Att("photo.png", "image/png", Png())], Limits(null), ocr);

        digest.Skipped.ShouldBe([new SkippedAttachment("photo.png", AttachmentType.Image, SkipReason.Disabled)]);
        ocr.Images.ShouldBeEmpty();
    }

    [Fact]
    public void Settings_types_and_limits_agree_on_vision_without_a_model()
    {
        var vision = new AttachmentSettings { ImageMode = ImageMode.Vision };

        vision.EnabledTypes().ShouldNotContain(AttachmentType.Image);
        vision.ToLimits().Images.ShouldBeNull();
        vision.EnabledTypes("vision-model-a").ShouldContain(AttachmentType.Image);
        vision.ToLimits("vision-model-a", BaseUrl).Images.ShouldBe(Vision);
        new AttachmentSettings().ToLimits(null, BaseUrl).Images.ShouldBe(Ocr);
    }

    [Fact]
    public void Image_timeout_defaults_to_the_model_timeout_for_vision_and_a_shorter_one_for_ocr()
    {
        var model = TimeSpan.FromSeconds(180);

        new AttachmentOptions().ImageTimeoutFor(ImageMode.Vision, model).ShouldBe(model);
        new AttachmentOptions().ImageTimeoutFor(ImageMode.Ocr, model).ShouldBe(AttachmentOptions.DefaultOcrTimeout);
        new AttachmentOptions { ImageTimeout = TimeSpan.FromSeconds(5) }.ImageTimeoutFor(ImageMode.Vision, model).ShouldBe(TimeSpan.FromSeconds(5));
        // Compose passes a blank Attachments__ImageTimeout through when .env doesn't set it.
        new ConfigurationBuilder().AddInMemoryCollection([new("ImageTimeout", "")]).Build().Get<AttachmentOptions>()!.ImageTimeout.ShouldBeNull();
    }

    [Fact]
    public async Task Reading_longer_than_the_image_timeout_is_a_failed_attachment()
    {
        var slow = new StubOcr(hang: true);

        var digest = await ConvertAllAsync(
            [Att("slow.png", "image/png", Png()), Att("fast.png", "image/png", Png())], Limits(Ocr), slow, timeout: TimeSpan.FromMilliseconds(50));

        digest.Skipped.Select(s => s.SkipReason).ShouldBe([SkipReason.Failed, SkipReason.Failed]);
        await Should.ThrowAsync<TimeoutException>(() => Reader(slow, timeout: TimeSpan.FromMilliseconds(50)).ReadAsync(Png(), Ocr, Ct));
    }

    [Fact]
    public async Task Images_over_the_image_limit_are_skipped_before_ocr()
    {
        var ocr = new StubOcr(_ => "text");
        var limits = Limits(Ocr) with { MaxImageBytes = 100 };

        var digest = await ConvertAllAsync([Att("big.png", "image/png", Png())], limits, ocr);

        digest.Skipped.ShouldBe([new SkippedAttachment("big.png", AttachmentType.Image, SkipReason.TooLarge)]);
        ocr.Images.ShouldBeEmpty();
    }

    [Fact]
    public async Task Images_declaring_more_pixels_than_the_cap_are_too_large_and_never_decoded()
    {
        var ocr = new StubOcr(_ => "text");
        var llm = new FakeLlmClientFactory();
        (string, string, byte[])[] bombs =
        [
            Att("a.png", "image/png", ImageHeaderTests.Png(30_000, 30_000)),
            Att("b.jpg", "image/jpeg", ImageHeaderTests.Jpeg(20_000, 20_000)),
            Att("c.webp", "image/webp", ImageHeaderTests.WebPLossless(16_384, 16_384)),
        ];

        var byOcr = await ConvertAllAsync([.. bombs, Att("d.gif", "image/gif", ImageHeaderTests.Gif(4_000, 4_000, frames: 3))], Limits(Ocr), ocr);
        var byVision = await ConvertAllAsync(bombs, Limits(Vision), ocr, llm);

        byOcr.Skipped.Select(s => s.SkipReason).ShouldBe(Enumerable.Repeat(SkipReason.TooLarge, 4));
        byVision.Skipped.Select(s => s.SkipReason).ShouldBe(Enumerable.Repeat(SkipReason.TooLarge, 3));
        ocr.Images.ShouldBeEmpty();
        llm.Chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Images_whose_header_cannot_be_read_fail_without_being_decoded()
    {
        var ocr = new StubOcr(_ => "text");
        var llm = new FakeLlmClientFactory();
        var truncated = Png()[..20];

        var byOcr = await ConvertAllAsync([Att("a.png", "image/png", truncated), Att("b.bin", "image/png", [.. new byte[64]])], Limits(Ocr), ocr);
        var byVision = await ConvertAllAsync([Att("a.png", "image/png", truncated)], Limits(Vision), ocr, llm);

        byOcr.Skipped.Select(s => s.SkipReason).ShouldBe([SkipReason.Failed, SkipReason.Failed]);
        byVision.Skipped.Single().SkipReason.ShouldBe(SkipReason.Failed);
        ocr.Images.ShouldBeEmpty();
        llm.Chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Policy_reads_images_by_ocr_by_default_and_by_the_vision_model_when_one_is_chosen()
    {
        var store = new InMemorySettingsStore();

        var ocr = await new AttachmentPolicy(store, NullLogger<AttachmentPolicy>.Instance).GetAsync(Ct);
        store.Current = store.Current with { VisionModel = "vision-model-a", Attachments = new AttachmentSettings { ImageMode = ImageMode.Vision } };
        var vision = await new AttachmentPolicy(store, NullLogger<AttachmentPolicy>.Instance).GetAsync(Ct);

        ocr.EnabledTypes.ShouldContain(AttachmentType.Image);
        ocr.Limits.Images.ShouldBe(Ocr);
        vision.EnabledTypes.ShouldContain(AttachmentType.Image);
        vision.Limits.Images.ShouldBe(Vision);
    }

    [Fact]
    public async Task Vision_mode_without_a_model_skips_images_as_disabled_and_leaves_scans_unread()
    {
        var store = new InMemorySettingsStore();
        store.Current = store.Current with { Attachments = new AttachmentSettings { ImageMode = ImageMode.Vision } };
        var policy = await new AttachmentPolicy(store, NullLogger<AttachmentPolicy>.Instance).GetAsync(Ct);
        var ocr = new StubOcr(_ => "text");
        var llm = new FakeLlmClientFactory();
        var scan = SyntheticImage.ScannedPdf(1);

        var digest = await ConvertAllAsync(
            Gmail(Att("photo.png", "image/png", Png()), Att("scan.pdf", "application/pdf", scan)), policy.EnabledTypes, policy.Limits, ocr, llm);

        policy.Limits.Images.ShouldBeNull();
        digest.Skipped.ShouldBe([new SkippedAttachment("photo.png", AttachmentType.Image, SkipReason.Disabled)]);
        digest.Converted.Single().Markdown.ShouldBeEmpty();
        ocr.Images.ShouldBeEmpty();
        llm.Chat.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, "image/jpeg")]
    [InlineData(false, "image/png")]
    public async Task Scanned_pdf_pages_are_read_as_images_up_to_the_page_limit(bool asJpeg, string expectedFormat)
    {
        var ocr = new StubOcr(image => image.Length > 0 ? $"text {ImageTextReader.MediaType(image)}" : "");

        var digest = await ConvertAllAsync([Att("scan.pdf", "application/pdf", SyntheticImage.ScannedPdf(4, asJpeg))], Limits(Ocr), ocr);

        ocr.Images.Count.ShouldBe(3);
        digest.Converted.Single().Markdown.ShouldBe(
            $"Scanned PDF without a text layer; page(s) 1, 2, 3 read by OCR.\n\ntext {expectedFormat}{PdfAttachmentConverter.PageBreak}"
            + $"text {expectedFormat}{PdfAttachmentConverter.PageBreak}text {expectedFormat}");
    }

    [Fact]
    public async Task Pdf_with_a_text_layer_is_never_read_as_images()
    {
        var ocr = new StubOcr(_ => "ocr text");

        var digest = await ConvertAllAsync(
            [Att("text.pdf", "application/pdf", SyntheticPdf.CreatePages(["Synthetic text layer"]))], Limits(Ocr), ocr);

        digest.Converted.Single().Markdown.ShouldBe("Synthetic text layer");
        ocr.Images.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_ocr_engine_fails_the_image_and_leaves_a_scanned_pdf_unread_not_failed()
    {
        var engine = Tesseract(path: "tesseract-not-installed-example");

        await Should.ThrowAsync<OcrUnavailableException>(() => engine.ReadTextAsync(Png(), Ct));
        var digest = await ConvertAllAsync(
            [Att("photo.png", "image/png", Png()), Att("scan.pdf", "application/pdf", SyntheticImage.ScannedPdf(2))], Limits(Ocr), engine);
        digest.Skipped.Single().ShouldBe(new SkippedAttachment("photo.png", AttachmentType.Image, SkipReason.Failed));
        digest.Converted.Single().Markdown.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_ocr_engine_leaves_a_scanned_pdf_unread_even_when_its_pages_fail_the_header_check()
    {
        var engine = Tesseract(path: "tesseract-not-installed-example");
        // The PDF dict says 100 x 100, the JPEG header 20000 x 20000: with an engine this page would be unreadable.
        var pdf = SyntheticImage.PdfWithImage(100, 100, "DCTDecode", ImageHeaderTests.Jpeg(20_000, 20_000));

        var digest = await ConvertAllAsync([Att("scan.pdf", "application/pdf", pdf)], Limits(Ocr), engine);

        digest.Converted.Single().Markdown.ShouldBeEmpty();
        digest.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_ocr_engine_takes_precedence_over_the_header_check_in_ocr_mode_only()
    {
        var unavailable = new StubOcr(available: false);
        var bomb = ImageHeaderTests.Png(30_000, 30_000);

        await Should.ThrowAsync<OcrUnavailableException>(() => Reader(unavailable).ReadAsync(bomb, Ocr, Ct));
        await Should.ThrowAsync<OcrUnavailableException>(() => Reader(unavailable).ReadAsync(Png()[..20], Ocr, Ct));
        var skipped = await Should.ThrowAsync<AttachmentSkippedException>(() => Reader(unavailable).ReadAsync(bomb, Vision, Ct));

        skipped.Reason.ShouldBe(SkipReason.TooLarge);
        unavailable.Images.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scanned_page_that_fails_is_noted_and_the_other_pages_are_kept()
    {
        var calls = 0;
        var ocr = new StubOcr(_ => ++calls == 2 ? throw new InvalidOperationException("exit 1") : $"page text {calls}");

        var digest = await ConvertAllAsync([Att("scan.pdf", "application/pdf", SyntheticImage.ScannedPdf(3))], Limits(Ocr), ocr);

        var pb = PdfAttachmentConverter.PageBreak;
        digest.Converted.Single().Markdown.ShouldBe(
            $"Scanned PDF without a text layer; page(s) 1, 3 read by OCR.\n\npage text 1{pb}[page 2 unreadable]{pb}page text 3");
    }

    [Fact]
    public async Task Header_names_the_pages_read_and_says_when_it_stopped_at_the_character_limit()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(612, 792);
        for (var i = 0; i < 3; i++)
        {
            builder.AddPage(612, 792).AddJpeg(SyntheticImage.Jpeg, new PdfRectangle(36, 600, 456, 730));
        }

        var pdf = builder.Build();
        var ocr = new StubOcr(_ => new string('x', 200));

        var all = await ConvertAllAsync([Att("scan.pdf", "application/pdf", pdf)], Limits(Ocr), ocr);
        var cut = await ConvertAllAsync([Att("scan.pdf", "application/pdf", pdf)], Limits(Ocr) with { MaxChars = 190 }, ocr);

        all.Converted.Single().Markdown.ShouldStartWith("Scanned PDF without a text layer; page(s) 2, 3 read by OCR.\n\n");
        cut.Converted.Single().Markdown.ShouldStartWith("Scanned PDF without a text layer; page(s) 2 read by OCR, stopped after page 2 at the character limit.");
    }

    [Fact]
    public async Task Page_image_declared_over_the_pixel_cap_is_never_decoded()
    {
        using var zeros = new MemoryStream();
        using (var deflate = new ZLibStream(zeros, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(new byte[100_000]);
        }

        var bomb = SyntheticImage.PdfWithImage(60_000, 60_000, "FlateDecode", zeros.ToArray());
        var ocr = new StubOcr(_ => "text");

        var digest = await ConvertAllAsync([Att("bomb.pdf", "application/pdf", bomb)], Limits(Ocr), ocr);

        digest.Converted.Single().Markdown.ShouldBe("Scanned PDF without a text layer; no page could be read.\n\n[page 1 unreadable]");
        ocr.Images.ShouldBeEmpty();
    }

    [Fact]
    public async Task Jpeg_2000_pages_go_to_ocr_as_stored_but_never_to_the_vision_model()
    {
        var jp2 = ImageHeaderTests.Jp2(420, 130, boxed: true);
        var pdf = SyntheticImage.PdfWithImage(420, 130, "JPXDecode", jp2);
        var ocr = new StubOcr(_ => "text");
        var llm = new FakeLlmClientFactory();

        var byOcr = await ConvertAllAsync([Att("scan.pdf", "application/pdf", pdf)], Limits(Ocr), ocr);
        var byVision = await ConvertAllAsync([Att("scan.pdf", "application/pdf", pdf)], Limits(Vision), ocr, llm);

        ocr.Images.Single().ShouldBe(jp2);
        byOcr.Converted.Single().Markdown.ShouldStartWith("Scanned PDF without a text layer; page(s) 1 read by OCR.");
        byVision.Converted.Single().Markdown.ShouldBe("Scanned PDF without a text layer; no page could be read.\n\n[page 1 unreadable]");
        llm.Chat.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tesseract_reads_rendered_synthetic_text(bool jpeg)
    {
        Assert.SkipUnless(TesseractInstalled.Value, "Tesseract is not installed (CI and the api image have it).");

        var text = await Tesseract().ReadTextAsync(jpeg ? SyntheticImage.Jpeg : SyntheticImage.Png, Ct);

        foreach (var line in SyntheticImage.Lines)
        {
            text.ShouldContain(line);
        }
    }

    [Fact]
    public async Task Tesseract_reads_a_scanned_pdf_end_to_end()
    {
        Assert.SkipUnless(TesseractInstalled.Value, "Tesseract is not installed (CI and the api image have it).");
        var scan = SyntheticImage.ScannedPdf(1);

        var digest = await ConvertAllAsync([Att("scan.pdf", "application/pdf", scan)], Limits(Ocr), Tesseract());

        digest.Converted.Single().Markdown.ShouldContain("billing@example.com");
    }

    /// <summary>
    /// The OS bound behind the header check: a 39.7 MP RGB PNG passes the 40 MP cap, yet under a 256 MiB address-space
    /// limit Leptonica can't allocate its canvas (measured: fails at 384 MiB, reads at 512 MiB), so Tesseract exits
    /// non-zero and the image is a failed attachment, while the next image reads normally in a fresh process.
    /// </summary>
    [Fact]
    public async Task Tesseract_under_the_memory_limit_fails_an_image_the_header_check_let_through_and_keeps_reading()
    {
        Assert.SkipUnless(TesseractInstalled.Value && OperatingSystem.IsLinux(), "Needs Tesseract and prlimit (Linux: CI and the api image).");
        var engine = Tesseract(memoryLimitMb: 256);
        var big = SyntheticImage.BlankPng(6_300, 6_300);
        ImageHeader.DeclaredPixels(big).ShouldBe(6_300L * 6_300);

        var digest = await ConvertAllAsync([Att("big.png", "image/png", big), Att("small.png", "image/png", Png())], Limits(Ocr), engine);

        digest.Skipped.ShouldBe([new SkippedAttachment("big.png", AttachmentType.Image, SkipReason.Failed)]);
        digest.Converted.Single().Markdown.ShouldContain("billing@example.com");
        (await Should.ThrowAsync<InvalidOperationException>(() => engine.ReadTextAsync(big, Ct))).Message.ShouldContain("exited with code");
    }

    private static readonly Lazy<bool> TesseractInstalled = new(() =>
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

    private static byte[] Png() => SyntheticImage.Png;

    private static TesseractOcrEngine Tesseract(string path = "tesseract", int memoryLimitMb = AttachmentOptions.DefaultOcrMemoryLimitMb) =>
        new(Options.Create(new AttachmentOptions { TesseractPath = path, OcrMemoryLimitMb = memoryLimitMb }), NullLogger<TesseractOcrEngine>.Instance);

    private static ImageTextReader Reader(IOcrEngine ocr, FakeLlmClientFactory? llm = null, TimeSpan? timeout = null) =>
        new(ocr, new OllamaVisionClient(llm ?? new FakeLlmClientFactory()),
            Options.Create(new AttachmentOptions { ImageTimeout = timeout ?? TimeSpan.FromSeconds(30) }), Options.Create(new LlmOptions()));

    private static async Task<ConvertedAttachment> ConvertImageAsync(
        byte[] image, ConversionLimits limits, IOcrEngine ocr, FakeLlmClientFactory? llm = null)
    {
        using var stream = new MemoryStream(image);
        return await new ImageAttachmentConverter(Reader(ocr, llm))
            .ConvertAsync(new GmailAttachment("att-1", "scan.png", "image/png", image.Length), stream, limits, Ct);
    }

    private static AttachmentConversionService Service(
        IGmailClient gmail, IOcrEngine ocr, FakeLlmClientFactory? llm = null, TimeSpan? timeout = null)
    {
        var reader = Reader(ocr, llm, timeout);
        return new(
            gmail,
            [new PdfAttachmentConverter(scanReader: reader, maxOcrPages: 3), new ImageAttachmentConverter(reader)],
            NullLogger<AttachmentConversionService>.Instance);
    }

    private static async Task<AttachmentDigest> ConvertAllAsync(
        (string Filename, string MimeType, byte[] Content)[] attachments, ConversionLimits limits, IOcrEngine ocr,
        FakeLlmClientFactory? llm = null, TimeSpan? timeout = null) =>
        await ConvertAllAsync(Gmail(attachments), ImagesAndPdfs, limits, ocr, llm, timeout);

    private static async Task<AttachmentDigest> ConvertAllAsync(
        IGmailClient gmail, IReadOnlySet<AttachmentType> enabled, ConversionLimits limits, IOcrEngine ocr,
        FakeLlmClientFactory? llm = null, TimeSpan? timeout = null) =>
        await Service(gmail, ocr, llm, timeout).ConvertAllAsync(MessageId, await AttachmentsAsync(gmail), enabled, limits, Ct);

    private static async Task<GmailAttachment[]> AttachmentsAsync(IGmailClient gmail) =>
        [.. (await gmail.GetMessageContentAsync(MessageId, Ct)).ShouldNotBeNull().Attachments];

    private static FakeGmailClient Gmail(params (string Filename, string MimeType, byte[] Content)[] attachments) =>
        new(new FakeTokenStore(TimeProvider.System), [new FakeMessage(
            MessageId, "thread-1", "Sender <sender@example.com>", "Synthetic subject", DateTimeOffset.UnixEpoch, ["INBOX"],
            Attachments: [.. attachments.Select((a, i) => new FakeAttachment($"att-{i + 1}", a.Filename, a.MimeType, a.Content.Length, a.Content))])]);

    private static (string, string, byte[]) Att(string filename, string mimeType, byte[] content) => (filename, mimeType, content);

    /// <summary>
    /// Records each image and answers with <paramref name="answer"/>, or never answers when <paramref name="hang"/> is set;
    /// not installed when <paramref name="available"/> is false.
    /// </summary>
    private sealed class StubOcr(Func<byte[], string>? answer = null, bool hang = false, bool available = true) : IOcrEngine
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
