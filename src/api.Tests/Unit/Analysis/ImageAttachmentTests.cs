using System.IO.Compression;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using static GmailOrganiser.Tests.Unit.Analysis.ImageTestSupport;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>
/// The image converter (stubbed OCR, fake vision client), the policy's image mode and scanned PDFs. The real engine is
/// covered by <see cref="TesseractOcrEngineTests"/>.
/// </summary>
public sealed class ImageAttachmentTests
{
    [Fact]
    public async Task Ocr_text_follows_a_one_line_description()
    {
        var ocr = new StubOcr(_ => "\n  Total due: 12.00 EUR\nbilling@example.com \n");

        var result = await ConvertImageAsync(Png, Limits(Ocr), ocr);

        result.ShouldBe(new ConvertedAttachment(
            "scan.png", AttachmentType.Image, $"{ImageTextReader.OcrDescription}\n\nTotal due: 12.00 EUR\nbilling@example.com", false));
        ocr.Images.Single().ShouldBe(Png);
    }

    [Fact]
    public async Task Ocr_without_text_says_so()
    {
        var result = await ConvertImageAsync(Png, Limits(Ocr), new StubOcr(_ => " \n"));

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

        var digest = await ConvertAllAsync([Att("photo.png", "image/png", Png)], Limits(Vision), new StubOcr(_ => ""), llm);

        digest.Skipped.ShouldBe([new SkippedAttachment("photo.png", AttachmentType.Image, SkipReason.Failed)]);
    }

    [Fact]
    public async Task Vision_skips_formats_it_cannot_read_as_unsupported_without_calling_the_model()
    {
        var llm = new FakeLlmClientFactory();
        byte[] gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0, 0];
        byte[] tiff = [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0, 0, 0];

        var digest = await ConvertAllAsync(
            [Att("photo.heic", "image/heic", Heic), Att("a.gif", "image/gif", gif), Att("b.tif", "image/tiff", tiff)],
            Limits(Vision), new StubOcr(_ => ""), llm);

        digest.Skipped.Select(s => s.SkipReason).ShouldBe([SkipReason.Unsupported, SkipReason.Unsupported, SkipReason.Unsupported]);
        llm.Chat.Requests.ShouldBeEmpty();
        ImageTextReader.IsVisionFormat("image/webp").ShouldBeTrue();
    }

    [Fact]
    public async Task Ocr_skips_a_format_it_does_not_know_as_unsupported_whether_or_not_an_engine_is_installed()
    {
        var ocr = new StubOcr(_ => "text");
        var unavailable = new StubOcr(available: false);

        var digest = await ConvertAllAsync([Att("photo.heic", "image/heic", Heic), Att("photo.png", "image/png", Png)], Limits(Ocr), ocr);
        var skipped = await Should.ThrowAsync<AttachmentSkippedException>(() => Reader(unavailable).ReadAsync(Heic, Ocr, Ct));

        digest.Skipped.ShouldBe([new SkippedAttachment("photo.heic", AttachmentType.Image, SkipReason.Unsupported)]);
        digest.Converted.Single().Filename.ShouldBe("photo.png");
        skipped.Reason.ShouldBe(SkipReason.Unsupported);
        ocr.Images.Single().ShouldBe(Png);
    }

    /// <summary>
    /// GIF, TIFF, BMP and JPEG 2000 have no header check: they go to Tesseract as they are, bounded by its memory limit,
    /// so an animated GIF, a GIF without its trailer or a TIFF cut short is Tesseract's to accept or reject (#158).
    /// </summary>
    [Fact]
    public async Task Formats_without_a_header_check_go_to_ocr_as_they_are()
    {
        var ocr = new StubOcr(_ => "text");
        (string, string, byte[])[] images =
        [
            Att("banner.gif", "image/gif", Gif(600, 300, frames: 250, trailer: true)),
            Att("no-trailer.gif", "image/gif", [.. Gif(10, 10, frames: 1, trailer: false), 0, 0]),
            Att("short.tif", "image/tiff", [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0]),
            Att("a.bmp", "image/bmp", [.. "BM"u8, .. new byte[24]]),
            Att("a.jp2", "image/jp2", Jp2Stub),
        ];

        var digest = await ConvertAllAsync(images, Limits(Ocr), ocr);

        digest.Skipped.ShouldBeEmpty();
        digest.Converted.Select(c => c.Filename).ShouldBe(images.Select(i => i.Item1));
        ocr.Images.ShouldBe(images.Select(i => i.Item3));
    }

    [Fact]
    public async Task Image_converter_without_image_limits_is_a_disabled_skip_not_a_failure()
    {
        var ocr = new StubOcr(_ => "text");

        var digest = await ConvertAllAsync([Att("photo.png", "image/png", Png)], Limits(null), ocr);

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
            [Att("slow.png", "image/png", Png), Att("fast.png", "image/png", Png)], Limits(Ocr), slow, timeout: TimeSpan.FromMilliseconds(50));

        digest.Skipped.Select(s => s.SkipReason).ShouldBe([SkipReason.Failed, SkipReason.Failed]);
        await Should.ThrowAsync<TimeoutException>(() => Reader(slow, timeout: TimeSpan.FromMilliseconds(50)).ReadAsync(Png, Ocr, Ct));
    }

    [Fact]
    public async Task Images_over_the_image_limit_are_skipped_before_ocr()
    {
        var ocr = new StubOcr(_ => "text");
        var limits = Limits(Ocr) with { MaxImageBytes = 100 };

        var digest = await ConvertAllAsync([Att("big.png", "image/png", Png)], limits, ocr);

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

        var byOcr = await ConvertAllAsync(bombs, Limits(Ocr), ocr);
        var byVision = await ConvertAllAsync(bombs, Limits(Vision), ocr, llm);

        byOcr.Skipped.Select(s => s.SkipReason).ShouldBe(Enumerable.Repeat(SkipReason.TooLarge, 3));
        byVision.Skipped.Select(s => s.SkipReason).ShouldBe(Enumerable.Repeat(SkipReason.TooLarge, 3));
        ocr.Images.ShouldBeEmpty();
        llm.Chat.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Images_whose_header_cannot_be_read_fail_without_being_decoded()
    {
        var ocr = new StubOcr(_ => "text");
        var llm = new FakeLlmClientFactory();
        var truncated = Png[..20];

        var byOcr = await ConvertAllAsync([Att("a.png", "image/png", truncated), Att("b.jpg", "image/jpeg", [0xFF, 0xD8, 0xFF, 0xD9])], Limits(Ocr), ocr);
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
            Message(Att("photo.png", "image/png", Png), Att("scan.pdf", "application/pdf", scan)), policy.EnabledTypes, policy.Limits, ocr, llm);

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
        await Should.ThrowAsync<OcrUnavailableException>(() => Reader(unavailable).ReadAsync(Png[..20], Ocr, Ct));
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
        var pdf = SyntheticImage.PdfWithImage(420, 130, "JPXDecode", Jp2Stub);
        var ocr = new StubOcr(_ => "text");
        var llm = new FakeLlmClientFactory();

        var byOcr = await ConvertAllAsync([Att("scan.pdf", "application/pdf", pdf)], Limits(Ocr), ocr);
        var byVision = await ConvertAllAsync([Att("scan.pdf", "application/pdf", pdf)], Limits(Vision), ocr, llm);

        ocr.Images.Single().ShouldBe(Jp2Stub);
        byOcr.Converted.Single().Markdown.ShouldStartWith("Scanned PDF without a text layer; page(s) 1 read by OCR.");
        byVision.Converted.Single().Markdown.ShouldBe("Scanned PDF without a text layer; no page could be read.\n\n[page 1 unreadable]");
        llm.Chat.Requests.ShouldBeEmpty();
    }

    /// <summary>Bytes no reader recognises, as an iPhone HEIC would be to them.</summary>
    private static byte[] Heic => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    /// <summary>The JP2 signature box and nothing else; enough to be sniffed as JPEG 2000.</summary>
    private static byte[] Jp2Stub => [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A, 0, 0, 0, 0];

    /// <summary>A GIF89a of <paramref name="frames"/> image descriptors, with or without the trailer byte.</summary>
    private static byte[] Gif(ushort width, ushort height, int frames, bool trailer)
    {
        byte[] frame = [0x2C, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 2, 0];
        return [.. "GIF89a"u8, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0,
                .. Enumerable.Repeat(frame, frames).SelectMany(f => f), .. (trailer ? (byte[])[0x3B] : [])];
    }
}
