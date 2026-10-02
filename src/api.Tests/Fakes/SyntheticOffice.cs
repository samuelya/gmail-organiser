using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>Synthetic docx, xlsx and pptx files built in memory with the Open XML SDK; nothing is checked in.</summary>
public static class SyntheticOffice
{
    /// <summary>
    /// A cell for <see cref="Xlsx"/>: a string (shared), a number, or a number shown as a date (built-in format 14) or
    /// with another built-in number format.
    /// </summary>
    public sealed record XCell(string? Text = null, double? Number = null, bool Date = false, uint? Format = null)
    {
        public uint? NumberFormat => Format ?? (Date ? 14u : null);
    }

    /// <summary>Sheets in order; each row's cells start at column A unless a reference is given.</summary>
    public static byte[] Xlsx(params (string Name, IReadOnlyList<IReadOnlyList<XCell>> Rows)[] sheets)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook(new S.Sheets());
            var strings = workbook.AddNewPart<SharedStringTablePart>();
            strings.SharedStringTable = new S.SharedStringTable();
            var formats = sheets.SelectMany(s => s.Rows).SelectMany(r => r).Select(c => c.NumberFormat).OfType<uint>().Distinct().ToList();
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(
                new S.Fonts(new S.Font()), new S.Fills(new S.Fill()), new S.Borders(new S.Border()),
                new S.CellFormats([new S.CellFormat(), .. formats.Select(f => new S.CellFormat { NumberFormatId = f, ApplyNumberFormat = true })]));

            var sheetId = 1u;
            foreach (var (name, rows) in sheets)
            {
                var part = workbook.AddNewPart<WorksheetPart>();
                var data = new S.SheetData();
                var rowIndex = 1u;
                foreach (var cells in rows)
                {
                    var row = new S.Row { RowIndex = rowIndex };
                    var column = 0;
                    foreach (var cell in cells)
                    {
                        var reference = $"{(char)('A' + column)}{rowIndex}";
                        column++;
                        if (cell.Text is not null)
                        {
                            strings.SharedStringTable.AppendChild(new S.SharedStringItem(new S.Text(cell.Text)));
                            row.AppendChild(new S.Cell
                            {
                                CellReference = reference,
                                DataType = S.CellValues.SharedString,
                                CellValue = new S.CellValue(strings.SharedStringTable.ChildElements.Count - 1),
                            });
                        }
                        else if (cell.Number is { } number)
                        {
                            row.AppendChild(new S.Cell
                            {
                                CellReference = reference,
                                CellValue = new S.CellValue(number),
                                StyleIndex = cell.NumberFormat is { } format ? (uint)formats.IndexOf(format) + 1 : null,
                            });
                        }
                    }

                    data.AppendChild(row);
                    rowIndex++;
                }

                part.Worksheet = new S.Worksheet(data);
                workbook.Workbook.Sheets!.AppendChild(new S.Sheet { Id = workbook.GetIdOfPart(part), SheetId = sheetId++, Name = name });
            }
        }

        return stream.ToArray();
    }

    /// <summary>A docx: a title, a heading, paragraphs, one bulleted item and a table, in that order.</summary>
    public static byte[] Docx(string title, string heading, IReadOnlyList<string> paragraphs, string listItem, IReadOnlyList<IReadOnlyList<string>> table)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(
                new W.Style(new W.StyleName { Val = "Title" }) { StyleId = "SynthTitle", Type = W.StyleValues.Paragraph },
                new W.Style(new W.StyleName { Val = "heading 2" }) { StyleId = "SynthHeading", Type = W.StyleValues.Paragraph });

            var body = new W.Body(
                Paragraph(title, style: "SynthTitle"),
                Paragraph(heading, style: "SynthHeading"));
            foreach (var text in paragraphs)
            {
                body.AppendChild(Paragraph(text));
            }

            body.AppendChild(Paragraph(listItem, listed: true));
            body.AppendChild(new W.Table(table.Select(r => new W.TableRow(r.Select(c => new W.TableCell(Paragraph(c)))))));
            main.Document = new W.Document(body);
        }

        return stream.ToArray();
    }

    /// <summary>A docx whose document.xml body and styles.xml are the given raw WordprocessingML.</summary>
    public static byte[] DocxXml(string body, string styles = "")
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            Feed(main, $"<w:document {WordNamespaces}><w:body>{body}</w:body></w:document>");
            Feed(main.AddNewPart<StyleDefinitionsPart>(), $"<w:styles {WordNamespaces}>{styles}</w:styles>");
        }

        return stream.ToArray();
    }

    /// <summary>The namespaces <see cref="DocxXml"/> declares: w, mc, wp, a, wps, v.</summary>
    private const string WordNamespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" mc:Ignorable=\"wps\"";

    /// <summary>A copy of an xlsx with the workbook switched to the 1904 date system.</summary>
    public static byte[] WithDate1904(byte[] xlsx) => Edit(xlsx, stream =>
    {
        using var document = SpreadsheetDocument.Open(stream, true);
        var workbook = document.WorkbookPart!.Workbook!;
        workbook.InsertAt(new S.WorkbookProperties { Date1904 = true }, 0);
    });

    /// <summary>A copy of an xlsx whose shared-string table is the given raw XML (for example, malformed past a point).</summary>
    public static byte[] WithSharedStringsXml(byte[] xlsx, string sst) => Edit(xlsx, stream =>
    {
        using var document = SpreadsheetDocument.Open(stream, true);
        Feed(document.WorkbookPart!.SharedStringTablePart!, sst);
    });

    private static byte[] Edit(byte[] file, Action<Stream> edit)
    {
        using var stream = new MemoryStream();
        stream.Write(file);
        edit(stream);
        return stream.ToArray();
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        part.FeedData(data);
    }

    /// <summary>A pptx with one slide per entry; each string is one text paragraph in one shape.</summary>
    public static byte[] Pptx(params IReadOnlyList<string>[] slides)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
        {
            var presentation = document.AddPresentationPart();
            presentation.Presentation = new P.Presentation(new P.SlideIdList());
            var slideId = 256u;
            foreach (var lines in slides)
            {
                var part = presentation.AddNewPart<SlidePart>();
                var textBody = new P.TextBody(new D.BodyProperties(), new D.ListStyle());
                foreach (var line in lines)
                {
                    textBody.AppendChild(new D.Paragraph(new D.Run(new D.Text(line))));
                }

                part.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(),
                    new P.Shape(
                        new P.NonVisualShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 2, Name = "Text" }, new P.NonVisualShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties()),
                        new P.ShapeProperties(),
                        textBody))));
                presentation.Presentation.SlideIdList!.AppendChild(new P.SlideId { Id = slideId++, RelationshipId = presentation.GetIdOfPart(part) });
            }
        }

        return stream.ToArray();
    }

    private static W.Paragraph Paragraph(string text, string? style = null, bool listed = false)
    {
        var properties = new W.ParagraphProperties();
        if (style is not null)
        {
            properties.ParagraphStyleId = new W.ParagraphStyleId { Val = style };
        }

        if (listed)
        {
            properties.NumberingProperties = new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = 1 });
        }

        return new W.Paragraph(properties, new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }
}
