using System.Text;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static GmailOrganiser.Tests.Fakes.SyntheticOffice;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>The docx, xlsx, pptx, csv and text converters on fixtures generated in memory.</summary>
public sealed class OfficeAttachmentConverterTests
{
    private const string MessageId = "msg-office-0001";
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private static readonly ConversionLimits Limits = new AttachmentSettings().ToLimits();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlainText_is_returned_as_is_without_the_bom_and_with_unix_line_endings()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("# Synthetic notes\r\nSee example.com | twice\r\n")).ToArray();

        var markdown = await ConvertAsync(new PlainTextAttachmentConverter(), bytes, "notes.txt");

        markdown.ShouldBe("# Synthetic notes\nSee example.com | twice");
    }

    [Fact]
    public async Task PlainText_honours_a_utf16_bom_and_stops_one_character_past_the_limit()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(new string('z', 5000))).ToArray();

        var markdown = await ConvertAsync(new PlainTextAttachmentConverter(), bytes, "notes.txt", Limits with { MaxChars = 600 });

        markdown.ShouldBe(new string('z', 601));
    }

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData("\t")]
    public async Task Csv_sniffs_the_delimiter_and_renders_a_table(string delimiter)
    {
        string[][] rows = [["Name", "Amount", "Note"], ["Alpha", "12.50", ""], ["Beta", "7", "via example.com"]];
        var csv = string.Join("\r\n", rows
            .Select(r => string.Join(delimiter, r)));

        var markdown = await ConvertAsync(new CsvAttachmentConverter(), Encoding.UTF8.GetBytes(csv), "data.csv");

        markdown.ShouldBe("| Name | Amount | Note |\n| --- | --- | --- |\n| Alpha | 12.50 | |\n| Beta | 7 | via example.com |");
    }

    [Fact]
    public async Task Csv_handles_quotes_pipes_line_breaks_ragged_rows_and_a_bom()
    {
        var csv = "﻿" + "Item;Detail\n\"A;1\";\"Says \"\"hi\"\"\"\n\nB|2;\"two\nlines\";extra\n";

        var markdown = await ConvertAsync(new CsvAttachmentConverter(), Encoding.UTF8.GetBytes(csv), "data.csv");

        markdown.ShouldBe("| Item | Detail | |\n| --- | --- | --- |\n| A;1 | Says \"hi\" | |\n| B\\|2 | two lines | extra |");
    }

    [Fact]
    public async Task Csv_keeps_the_first_rows_and_says_more_were_left_out()
    {
        var csv = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"row{i},{i}"));

        var markdown = await ConvertAsync(new CsvAttachmentConverter(maxSheetRows: 3), Encoding.UTF8.GetBytes(csv), "data.csv");

        markdown.ShouldBe("| row1 | 1 |\n| --- | --- |\n| row2 | 2 |\n| row3 | 3 |\n\n_Only the first 3 rows are shown._");
    }

    [Fact]
    public async Task Csv_exactly_at_the_row_cap_has_no_note()
    {
        var markdown = await ConvertAsync(new CsvAttachmentConverter(maxSheetRows: 2), Encoding.UTF8.GetBytes("a,b\n1,2\n\n"), "data.csv");

        markdown.ShouldNotContain("Only the first");
    }

    [Fact]
    public async Task Xlsx_renders_each_sheet_under_its_name_with_invariant_numbers_and_iso_dates()
    {
        var xlsx = Xlsx(
            ("Invoices", [
                [new XCell("Customer"), new XCell("Total"), new XCell("Due")],
                [new XCell("alpha@example.com"), new XCell(Number: 1234.5), new XCell(Number: 45000, Date: true)],
                [new XCell("A | B"), new XCell(Number: 0.1 + 0.2), new XCell()],
            ]),
            ("Empty", []),
            ("Notes", [[new XCell("Synthetic note")]]));

        var markdown = await ConvertAsync(new SpreadsheetAttachmentConverter(), xlsx, "book.xlsx");

        markdown.ShouldBe(
            "## Invoices\n\n| Customer | Total | Due |\n| --- | --- | --- |\n| alpha@example.com | 1234.5 | 2023-03-15 |\n| A \\| B | 0.3 | |\n\n" +
            "## Notes\n\n| Synthetic note |\n| --- |");
    }

    [Fact]
    public async Task Xlsx_reads_only_the_row_cap_per_sheet()
    {
        var rows = Enumerable.Range(1, 50).Select(i => (IReadOnlyList<XCell>)[new XCell($"r{i}"), new XCell(Number: i)]).ToList();

        var markdown = await ConvertAsync(new SpreadsheetAttachmentConverter(maxSheetRows: 4), Xlsx(("Data", rows)), "book.xlsx");

        markdown.Split('\n').Count(l => l.StartsWith("| r", StringComparison.Ordinal)).ShouldBe(4);
        markdown.ShouldEndWith("_Only the first 4 rows are shown._");
    }

    [Fact]
    public async Task Docx_maps_headings_lists_paragraphs_and_tables()
    {
        var docx = Docx("Synthetic report", "Summary", ["First paragraph for example.com.", ""], "Point one",
            [["Item", "Cost"], ["Widget | small", "3"]]);

        var markdown = await ConvertAsync(new WordDocumentAttachmentConverter(), docx, "report.docx");

        markdown.ShouldBe(
            "# Synthetic report\n\n## Summary\n\nFirst paragraph for example.com.\n\n- Point one\n\n" +
            "| Item | Cost |\n| --- | --- |\n| Widget \\| small | 3 |");
    }

    [Fact]
    public async Task Docx_stops_reading_past_the_character_limit()
    {
        var paragraphs = Enumerable.Range(1, 200).Select(i => $"Synthetic paragraph {i} for example.com").ToList();
        var docx = Docx("Title", "Heading", paragraphs, "item", [["a"]]);

        var markdown = await ConvertAsync(new WordDocumentAttachmentConverter(), docx, "long.docx", Limits with { MaxChars = 600 });

        markdown.Length.ShouldBeInRange(601, 700);
        markdown.ShouldNotContain("paragraph 200");
    }

    [Fact]
    public async Task Pptx_lists_slide_text_in_order_and_skips_slides_without_text()
    {
        var pptx = Pptx(["Synthetic deck", "example.com"], [], ["Next steps", " ", "Ship it"]);

        var markdown = await ConvertAsync(new PresentationAttachmentConverter(), pptx, "deck.pptx");

        markdown.ShouldBe("## Slide 1\n\nSynthetic deck\nexample.com\n\n## Slide 3\n\nNext steps\nShip it");
    }

    public static TheoryData<IAttachmentConverter> OpenXmlConverters =>
        [new SpreadsheetAttachmentConverter(), new WordDocumentAttachmentConverter(), new PresentationAttachmentConverter()];

    [Theory]
    [MemberData(nameof(OpenXmlConverters))]
    public async Task OpenXml_converters_throw_on_a_corrupt_file(IAttachmentConverter converter) =>
        await Should.ThrowAsync<Exception>(() => ConvertAsync(converter, Encoding.ASCII.GetBytes("PK not really a zip"), "broken"));

    [Fact]
    public async Task OpenXml_parse_past_the_timeout_throws_a_timeout()
    {
        var xlsx = Xlsx(("Data", [[new XCell("a")]]));

        await Should.ThrowAsync<TimeoutException>(() => ConvertAsync(new SpreadsheetAttachmentConverter(parseTimeout: TimeSpan.Zero), xlsx, "slow.xlsx"));
    }

    [Fact]
    public async Task Service_records_a_corrupt_office_file_as_failed_and_still_converts_the_rest()
    {
        var gmail = Gmail(
            ("broken.xlsx", XlsxMime, Encoding.ASCII.GetBytes("not a workbook")),
            ("ok.docx", DocxMime, Docx("Synthetic", "Part", ["Body text"], "item", [["x"]])));

        var digest = await ConvertAllAsync(gmail, Limits);

        digest.Skipped.ShouldBe([new SkippedAttachment("broken.xlsx", AttachmentType.Spreadsheet, SkipReason.Failed)]);
        digest.Converted.Single().Filename.ShouldBe("ok.docx");
    }

    [Fact]
    public async Task Service_truncates_a_long_spreadsheet_once()
    {
        var rows = Enumerable.Range(1, 200).Select(i => (IReadOnlyList<XCell>)[new XCell($"Synthetic row {i} at example.com")]).ToList();
        var gmail = Gmail(("big.xlsx", XlsxMime, Xlsx(("Data", rows))));

        var digest = await ConvertAllAsync(gmail, Limits with { MaxChars = 500 });

        var converted = digest.Converted.Single();
        converted.Truncated.ShouldBeTrue();
        converted.Markdown.Length.ShouldBeLessThanOrEqualTo(500);
        converted.Markdown.ShouldEndWith(ConversionLimits.TruncatedMarker);
    }

    [Fact]
    public void AddAttachments_registers_one_converter_per_type_with_the_configured_row_cap()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Attachments:MaxSheetRows"] = "7" }).Build();
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddAttachments().BuildServiceProvider();

        var converters = provider.GetServices<IAttachmentConverter>().ToList();

        foreach (var type in new[] { AttachmentType.Pdf, AttachmentType.WordDocument, AttachmentType.Spreadsheet, AttachmentType.Csv, AttachmentType.Presentation, AttachmentType.PlainText })
        {
            converters.Count(c => c.CanConvert(type)).ShouldBe(1, type.ToString());
        }

        converters.ShouldNotContain(c => c.CanConvert(AttachmentType.Archive));
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AttachmentOptions>>().Value.MaxSheetRows.ShouldBe(7);
    }

    private static async Task<string> ConvertAsync(IAttachmentConverter converter, byte[] bytes, string filename, ConversionLimits? limits = null)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var result = await converter.ConvertAsync(new GmailAttachment("att-1", filename, "application/octet-stream", bytes.Length), stream, limits ?? Limits, Ct);
        result.Filename.ShouldBe(filename);
        result.Truncated.ShouldBeFalse();
        return result.Markdown;
    }

    private static async Task<AttachmentDigest> ConvertAllAsync(IGmailClient gmail, ConversionLimits limits)
    {
        IAttachmentConverter[] converters = [new SpreadsheetAttachmentConverter(), new WordDocumentAttachmentConverter()];
        var attachments = (await gmail.GetMessageContentAsync(MessageId, Ct)).ShouldNotBeNull().Attachments;
        var enabled = new HashSet<AttachmentType> { AttachmentType.Spreadsheet, AttachmentType.WordDocument };
        return await new AttachmentConversionService(gmail, converters, NullLogger<AttachmentConversionService>.Instance)
            .ConvertAllAsync(MessageId, [.. attachments], enabled, limits, Ct);
    }

    private static CountingGmailClient Gmail(params (string Filename, string MimeType, byte[] Content)[] attachments)
    {
        var message = new FakeMessage(
            MessageId, "thread-1", "Sender <sender@example.com>", "Synthetic subject", DateTimeOffset.UnixEpoch, ["INBOX"],
            Attachments: [.. attachments.Select((a, i) => new FakeAttachment($"att-{i + 1}", a.Filename, a.MimeType, a.Content.Length, a.Content))]);
        return new CountingGmailClient(new FakeGmailClient(new FakeTokenStore(TimeProvider.System), [message]));
    }
}
