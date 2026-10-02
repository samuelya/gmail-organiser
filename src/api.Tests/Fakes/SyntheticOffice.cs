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
    /// <summary>A cell for <see cref="Xlsx"/>: a string (shared), a number, or a number shown as a date.</summary>
    public sealed record XCell(string? Text = null, double? Number = null, bool Date = false);

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
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(
                new S.Fonts(new S.Font()), new S.Fills(new S.Fill()), new S.Borders(new S.Border()),
                new S.CellFormats(new S.CellFormat(), new S.CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }));

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
                                StyleIndex = cell.Date ? 1u : null,
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
