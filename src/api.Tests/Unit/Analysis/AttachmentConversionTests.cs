using System.Net;
using System.Text;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>The type resolver, the PDF converter on generated synthetic PDFs, and the service's limits and skips.</summary>
public sealed class AttachmentConversionTests
{
    private const string MessageId = "msg-att-0001";
    private static readonly ConversionLimits Limits = ConversionLimits.Default;
    private static readonly IReadOnlySet<AttachmentType> PdfOnly = new HashSet<AttachmentType> { AttachmentType.Pdf };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("application/pdf", "a.bin", AttachmentType.Pdf)]
    [InlineData("application/octet-stream", "Statement.PDF", AttachmentType.Pdf)]
    [InlineData("image/jpeg", "photo.jpg", AttachmentType.Image)]
    [InlineData("image/heic", "", AttachmentType.Image)]
    [InlineData("application/octet-stream", "scan.tiff", AttachmentType.Image)]
    [InlineData("text/csv", "data.csv", AttachmentType.Csv)]
    [InlineData("text/csv; charset=utf-8", "data.txt", AttachmentType.Csv)]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "book.xlsx", AttachmentType.Spreadsheet)]
    [InlineData("application/vnd.ms-excel", "book.xls", AttachmentType.Spreadsheet)]
    [InlineData("", "book.ods", AttachmentType.Spreadsheet)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "letter.docx", AttachmentType.WordDocument)]
    [InlineData("application/msword", "letter.doc", AttachmentType.WordDocument)]
    [InlineData("application/vnd.openxmlformats-officedocument.presentationml.presentation", "deck.pptx", AttachmentType.Presentation)]
    [InlineData("text/plain", "notes.txt", AttachmentType.PlainText)]
    [InlineData("application/zip", "files.zip", AttachmentType.Archive)]
    [InlineData("application/x-7z-compressed", "files.7z", AttachmentType.Archive)]
    [InlineData("application/octet-stream", "files.tar.gz", AttachmentType.Archive)]
    [InlineData("application/octet-stream", "setup.exe", AttachmentType.Other)]
    [InlineData("application/x-msdownload", "setup.exe", AttachmentType.Other)]
    [InlineData("not a mime type", "noextension", AttachmentType.Other)]
    [InlineData(null, null, AttachmentType.Other)]
    public void Resolver_uses_the_mime_type_first_and_the_extension_second(string? mimeType, string? filename, AttachmentType expected) =>
        AttachmentTypeResolver.Resolve(mimeType, filename).ShouldBe(expected);

    [Fact]
    public async Task Pdf_text_layer_becomes_markdown_with_page_breaks()
    {
        var pdf = SyntheticPdf.CreatePages(["Synthetic invoice page one", "", "Synthetic invoice page two"]);

        var result = await ConvertPdfAsync(pdf, Limits);

        result.ShouldBe(new ConvertedAttachment("invoice.pdf", AttachmentType.Pdf, "Synthetic invoice page one\n\n---\n\nSynthetic invoice page two", false));
    }

    [Fact]
    public async Task Pdf_without_a_text_layer_is_empty_not_failed()
    {
        var result = await ConvertPdfAsync(SyntheticPdf.CreatePages(["", ""]), Limits);

        (result.Markdown, result.Truncated).ShouldBe(("", false));
    }

    [Fact]
    public async Task Pdf_stops_reading_past_the_character_limit_and_the_service_truncates_once()
    {
        var pdf = SyntheticPdf.CreatePages([.. Enumerable.Range(1, 20).Select(i => $"Synthetic statement line {i} for example.com")]);
        var limits = Limits with { MaxChars = 100 };

        var raw = await ConvertPdfAsync(pdf, limits);
        var gmail = Gmail(Att("statement.pdf", "application/pdf", pdf));
        var digest = await ConvertAllAsync(gmail, PdfOnly, limits);

        raw.Markdown.ShouldNotContain("line 20");
        var result = digest.Converted.Single();
        result.Truncated.ShouldBeTrue();
        result.Markdown.ShouldStartWith("Synthetic statement line 1 for example.com");
        result.Markdown.ShouldEndWith("\n" + ConversionLimits.TruncatedMarker);
        result.Markdown.Split(ConversionLimits.TruncatedMarker).Length.ShouldBe(2);
        result.Markdown.Length.ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Truncate_after_a_page_break_stays_within_the_limit_and_is_idempotent()
    {
        var limits = Limits with { MaxChars = 50 };
        var text = new string('a', 36) + PdfAttachmentConverter.PageBreak + new string('b', 100);

        var (once, truncated) = limits.Truncate(text);

        truncated.ShouldBeTrue();
        once.ShouldBe(new string('a', 36) + "\n" + ConversionLimits.TruncatedMarker);
        once.Length.ShouldBeLessThanOrEqualTo(50);
        limits.Truncate(once).ShouldBe((once, false));
    }

    [Fact]
    public void Truncate_never_splits_a_surrogate_pair()
    {
        var limits = Limits with { MaxChars = 20 };

        var (markdown, _) = limits.Truncate("1234567\U0001F600" + new string('x', 30));

        markdown.ShouldBe("1234567\n" + ConversionLimits.TruncatedMarker);
    }

    [Fact]
    public void Truncate_below_the_marker_length_yields_the_marker_alone()
    {
        var limits = Limits with { MaxChars = 5 };

        limits.Truncate("abcdefgh").ShouldBe((ConversionLimits.TruncatedMarker, true));
        limits.Truncate(ConversionLimits.TruncatedMarker).Markdown.ShouldBe(ConversionLimits.TruncatedMarker);
    }

    [Fact]
    public async Task Pdf_that_is_not_a_pdf_throws()
    {
        await Should.ThrowAsync<Exception>(() => ConvertPdfAsync(Encoding.ASCII.GetBytes("not a pdf at all"), Limits));
    }

    [Fact]
    public async Task Pdf_parse_past_the_timeout_throws_a_timeout_and_the_service_records_failed()
    {
        var pdf = SyntheticPdf.CreatePages([.. Enumerable.Range(1, 20).Select(i => $"Synthetic page {i}")]);
        using var stream = new MemoryStream(pdf);

        await Should.ThrowAsync<TimeoutException>(() => new PdfAttachmentConverter(TimeSpan.Zero).ConvertAsync(
            new GmailAttachment("att-1", "slow.pdf", "application/pdf", pdf.Length), stream, Limits, Ct));

        var gmail = Gmail(Att("slow.pdf", "application/pdf", pdf));
        var digest = await ConvertAllAsync(gmail, PdfOnly, Limits, new PdfAttachmentConverter(TimeSpan.Zero));
        digest.Skipped.ShouldBe([new SkippedAttachment("slow.pdf", AttachmentType.Pdf, SkipReason.Failed)]);
    }

    [Fact]
    public async Task Service_converts_enabled_pdfs_and_skips_the_rest_by_name_and_type()
    {
        var gmail = Gmail(
            Pdf("a.pdf", "Synthetic alpha text"),
            Att("photo.png", "image/png", [1, 2, 3]),
            Att("files.zip", "application/zip", [1, 2, 3]),
            Pdf("b.pdf", "Synthetic beta text"));
        var enabled = new HashSet<AttachmentType> { AttachmentType.Pdf, AttachmentType.Image };

        var digest = await ConvertAllAsync(gmail, enabled, Limits);

        digest.Converted.Select(c => (c.Filename, c.AttachmentType)).ShouldBe([("a.pdf", AttachmentType.Pdf), ("b.pdf", AttachmentType.Pdf)]);
        digest.Converted[0].Markdown.ShouldContain("Synthetic alpha text");
        digest.Skipped.ShouldBe(
        [
            new SkippedAttachment("photo.png", AttachmentType.Image, SkipReason.Unsupported),
            new SkippedAttachment("files.zip", AttachmentType.Archive, SkipReason.Disabled),
        ]);
        gmail.AttachmentContentCalls.ShouldBe(["att-1", "att-4"]);
    }

    [Fact]
    public async Task Service_skips_an_oversize_pdf_before_download()
    {
        var gmail = Gmail(Pdf("big.pdf", "Synthetic big text", minimumSize: 4096), Pdf("small.pdf", "Synthetic small text"));

        var digest = await ConvertAllAsync(gmail, PdfOnly, Limits with { MaxBytes = 2048 });

        digest.Skipped.ShouldBe([new SkippedAttachment("big.pdf", AttachmentType.Pdf, SkipReason.TooLarge)]);
        digest.Converted.Single().Filename.ShouldBe("small.pdf");
        gmail.ContentCalls.Count.ShouldBe(1);
        gmail.AttachmentContentCalls.ShouldBe(["att-2"]);
    }

    [Fact]
    public async Task Service_applies_the_per_message_cap_in_attachment_order()
    {
        var gmail = Gmail(Pdf("1.pdf", "One"), Att("x.zip", "application/zip", [1]), Pdf("2.pdf", "Two"), Pdf("3.pdf", "Three"));

        var digest = await ConvertAllAsync(gmail, PdfOnly, Limits with { MaxPerMessage = 2 });

        digest.Converted.Select(c => c.Filename).ShouldBe(["1.pdf", "2.pdf"]);
        digest.Skipped.ShouldBe(
        [
            new SkippedAttachment("x.zip", AttachmentType.Archive, SkipReason.Disabled),
            new SkippedAttachment("3.pdf", AttachmentType.Pdf, SkipReason.TooMany),
        ]);
        gmail.AttachmentContentCalls.ShouldBe(["att-1", "att-3"]);
    }

    [Fact]
    public async Task Service_records_a_converter_exception_as_failed_and_continues()
    {
        var gmail = Gmail(Att("broken.pdf", "application/pdf", Encoding.ASCII.GetBytes("not a pdf")), Pdf("ok.pdf", "Synthetic ok text"));

        var digest = await ConvertAllAsync(gmail, PdfOnly, Limits);

        digest.Skipped.ShouldBe([new SkippedAttachment("broken.pdf", AttachmentType.Pdf, SkipReason.Failed)]);
        digest.Converted.Single().Markdown.ShouldContain("Synthetic ok text");
    }

    [Fact]
    public async Task Service_records_a_failed_download_as_failed_and_converts_the_others()
    {
        var gmail = Gmail(Pdf("1.pdf", "Synthetic one"), Pdf("2.pdf", "Synthetic two"), Pdf("3.pdf", "Synthetic three"));
        var attachments = await AttachmentsAsync(gmail);
        var service = Service(gmail);

        var first = await service.ConvertAllAsync(MessageId, attachments[..1], PdfOnly, Limits, Ct);
        gmail.Inner.FailNext(HttpStatusCode.ServiceUnavailable, 1);
        var rest = await service.ConvertAllAsync(MessageId, attachments[1..], PdfOnly, Limits, Ct);

        first.Converted.Single().Filename.ShouldBe("1.pdf");
        rest.Skipped.ShouldBe([new SkippedAttachment("2.pdf", AttachmentType.Pdf, SkipReason.Failed)]);
        rest.Converted.Single().Filename.ShouldBe("3.pdf");
    }

    [Fact]
    public async Task Service_lets_a_lost_connection_and_cancellation_propagate()
    {
        var gmail = Gmail(Pdf("1.pdf", "Synthetic one"));
        var attachments = await AttachmentsAsync(gmail);

        gmail.Inner.FailNext(HttpStatusCode.Unauthorized, 1);
        await Should.ThrowAsync<GmailNotConnectedException>(() => Service(gmail).ConvertAllAsync(MessageId, attachments, PdfOnly, Limits, Ct));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => Service(gmail).ConvertAllAsync(MessageId, attachments, PdfOnly, Limits, cancelled.Token));
    }

    [Fact]
    public async Task Service_records_a_converter_timeout_as_failed()
    {
        var gmail = Gmail(Att("notes.txt", "text/plain", [1]));
        var throwing = new EchoConverter(() => throw new TaskCanceledException("A converter's own timeout"));

        var digest = await ConvertAllAsync(gmail, new HashSet<AttachmentType> { AttachmentType.PlainText }, Limits, throwing);

        digest.Skipped.ShouldBe([new SkippedAttachment("notes.txt", AttachmentType.PlainText, SkipReason.Failed)]);
    }

    [Fact]
    public async Task Service_converts_inline_content_without_a_download()
    {
        var gmail = Gmail();
        var pdf = SyntheticPdf.Create("Synthetic title", "Synthetic inline text");
        GmailAttachment[] attachments = [new(null, "inline.pdf", "application/pdf", pdf.Length, pdf)];

        var digest = await Service(gmail).ConvertAllAsync(MessageId, attachments, PdfOnly, Limits, Ct);

        digest.Converted.Single().Markdown.ShouldContain("Synthetic inline text");
        gmail.AttachmentContentCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_checks_an_unknown_size_after_the_download()
    {
        var gmail = Gmail(Pdf("big.pdf", "Synthetic big text", minimumSize: 4096));
        var attachments = (await AttachmentsAsync(gmail)).Select(a => a with { Size = null }).ToArray();

        var digest = await Service(gmail).ConvertAllAsync(MessageId, attachments, PdfOnly, Limits with { MaxBytes = 2048 }, Ct);

        digest.Skipped.ShouldBe([new SkippedAttachment("big.pdf", AttachmentType.Pdf, SkipReason.TooLarge)]);
        gmail.AttachmentContentCalls.ShouldBe(["att-1"]);
    }

    [Fact]
    public async Task Service_truncates_whatever_a_converter_returns()
    {
        var gmail = Gmail(Att("notes.txt", "text/plain", [1]));
        var echo = new EchoConverter(() => new string('x', 50));

        var digest = await ConvertAllAsync(gmail, new HashSet<AttachmentType> { AttachmentType.PlainText }, Limits with { MaxChars = 20 }, echo);

        digest.Converted.Single().ShouldBe(new ConvertedAttachment("notes.txt", AttachmentType.PlainText, "xxxxxxxx\n[truncated]", true));
    }

    [Fact]
    public async Task Service_for_a_message_without_attachments_downloads_nothing()
    {
        var gmail = Gmail();

        (await ConvertAllAsync(gmail, PdfOnly, Limits)).ShouldBe(AttachmentDigest.Empty);
        gmail.AttachmentContentCalls.ShouldBeEmpty();
    }

    private static async Task<ConvertedAttachment> ConvertPdfAsync(byte[] pdf, ConversionLimits limits)
    {
        using var stream = new MemoryStream(pdf);
        return await new PdfAttachmentConverter().ConvertAsync(
            new GmailAttachment("att-1", "invoice.pdf", "application/pdf", pdf.Length), stream, limits, Ct);
    }

    private static AttachmentConversionService Service(IGmailClient gmail, IAttachmentConverter? converter = null) =>
        new(gmail, [converter ?? new PdfAttachmentConverter()], NullLogger<AttachmentConversionService>.Instance);

    private static async Task<GmailAttachment[]> AttachmentsAsync(IGmailClient gmail) =>
        [.. (await gmail.GetMessageContentAsync(MessageId, Ct)).ShouldNotBeNull().Attachments];

    /// <summary>One <c>messages.get</c> for the list, as the analysis does, then the service.</summary>
    private static async Task<AttachmentDigest> ConvertAllAsync(
        IGmailClient gmail, IReadOnlySet<AttachmentType> enabled, ConversionLimits limits, IAttachmentConverter? converter = null) =>
        await Service(gmail, converter).ConvertAllAsync(MessageId, await AttachmentsAsync(gmail), enabled, limits, Ct);

    private static CountingGmailClient Gmail(params (string Filename, string MimeType, byte[] Content)[] attachments)
    {
        var message = new FakeMessage(
            MessageId, "thread-1", "Sender <sender@example.com>", "Synthetic subject", DateTimeOffset.UnixEpoch, ["INBOX"],
            Attachments: [.. attachments.Select((a, i) => new FakeAttachment($"att-{i + 1}", a.Filename, a.MimeType, a.Content.Length, a.Content))]);
        return new CountingGmailClient(new FakeGmailClient(new FakeTokenStore(TimeProvider.System), [message]));
    }

    private static (string, string, byte[]) Pdf(string filename, string text, int minimumSize = 0) =>
        (filename, "application/pdf", SyntheticPdf.Create("Synthetic title", text, minimumSize));

    private static (string, string, byte[]) Att(string filename, string mimeType, byte[] content) => (filename, mimeType, content);

    private sealed class EchoConverter(Func<string> markdown) : IAttachmentConverter
    {
        public bool CanConvert(AttachmentType type) => type == AttachmentType.PlainText;

        public Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct) =>
            Task.FromResult(new ConvertedAttachment(attachment.Filename, AttachmentType.Other, markdown(), false));
    }
}
