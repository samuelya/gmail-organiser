using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// An xlsx workbook as markdown: per worksheet a <c>## name</c> heading and a table of its first <c>maxSheetRows</c>
/// non-empty rows (Open XML SDK, MIT). Worksheets and the shared-string table are streamed, so a large sheet costs
/// only the rows shown and the strings up to the highest one they use. Numbers are invariant, date-formatted cells
/// become ISO dates (1900 or 1904 date system), time-only cells <c>HH:mm</c>. Legacy xls and ods aren't Open XML and
/// throw, which the caller records as failed. Plain-text content (a CSV sent as <c>application/vnd.ms-excel</c>) is
/// read as CSV. Reading stops once the text exceeds <see cref="ConversionLimits.MaxChars"/>.
/// </summary>
/// <param name="parseTimeout">Defaults to 30 s; a test seam.</param>
public sealed class SpreadsheetAttachmentConverter(int maxSheetRows = AttachmentOptions.DefaultMaxSheetRows, TimeSpan? parseTimeout = null)
    : IAttachmentConverter
{
    /// <summary>
    /// Built-in number formats that show a date or time (ECMA-376 18.8.30): 14–22 and 45–47, plus the East Asian
    /// locale formats 27–36 and 50–58.
    /// </summary>
    private static readonly HashSet<uint> BuiltInDateFormats =
        [.. Enumerable.Range(14, 9).Concat(Enumerable.Range(27, 10)).Concat(Enumerable.Range(45, 3)).Concat(Enumerable.Range(50, 9)).Select(i => (uint)i)];

    /// <summary>Days from 1899-12-30 (the 1900 system's OLE epoch) to 1904-01-01 (the 1904 system's day 0).</summary>
    private const double Date1904Offset = 1462;

    private readonly int maxSheetRows = maxSheetRows > 0 ? maxSheetRows : throw new ArgumentOutOfRangeException(nameof(maxSheetRows));
    private readonly TimeSpan parseTimeout = parseTimeout ?? ParseTimeout.Default;

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Spreadsheet;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        if (OfficeDocument.IsPlainText(content))
        {
            var csv = await ParseTimeout.RunAsync(t => CsvAttachmentConverter.Read(content, maxSheetRows, limits.MaxChars, t), parseTimeout, "CSV", ct);
            return new ConvertedAttachment(attachment.Filename, AttachmentType.Csv, csv, false);
        }

        var markdown = await ParseTimeout.RunAsync(t => Read(content, limits.MaxChars, t), parseTimeout, "spreadsheet", ct);
        return new ConvertedAttachment(attachment.Filename, AttachmentType.Spreadsheet, markdown, false);
    }

    private string Read(Stream content, int maxChars, CancellationToken ct)
    {
        using var document = SpreadsheetDocument.Open(content, false, OfficeDocument.ReadOnly);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("The workbook has no workbook part.");
        OfficeDocument.CheckDepth(workbook, ct);
        OfficeDocument.CheckDepth(workbook.WorkbookStylesPart, ct);
        OfficeDocument.CheckDepth(workbook.SharedStringTablePart, ct);
        using var strings = new SharedStrings(workbook.SharedStringTablePart, maxChars, ct);
        var cells = new CellReader(strings, DateStyles(workbook.WorkbookStylesPart?.Stylesheet),
            workbook.Workbook?.WorkbookProperties?.Date1904?.Value == true ? Date1904Offset : 0);
        var markdown = new StringBuilder();

        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
            {
                continue;
            }

            OfficeDocument.CheckDepth(part, ct);
            var rows = ReadRows(part, cells, maxChars - markdown.Length, ct, out var more);
            if (rows.Count == 0)
            {
                continue;
            }

            if (markdown.Length > 0)
            {
                markdown.Append('\n');
            }

            markdown.Append("## ").Append(sheet.Name?.Value?.ReplaceLineEndings(" ") ?? "").Append("\n\n");
            MarkdownTable.Append(markdown, rows);
            if (more)
            {
                markdown.Append('\n').Append(MarkdownTable.RowCapNote(maxSheetRows)).Append('\n');
            }

            if (markdown.Length > maxChars)
            {
                break;
            }
        }

        return markdown.ToString().TrimEnd();
    }

    private List<IReadOnlyList<string>> ReadRows(
        WorksheetPart part, CellReader cellReader, int maxChars, CancellationToken ct, out bool more)
    {
        var rows = new List<IReadOnlyList<string>>();
        var chars = 0;
        more = false;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            if (chars > maxChars)
            {
                break;
            }

            var row = (Row)reader.LoadCurrentElement()!;
            if (rows.Count >= maxSheetRows)
            {
                // Formatted but empty rows below the data are common; only a row with a value counts as left out.
                if (row.Elements<Cell>().Any(c => c.CellValue is not null || c.InlineString is not null))
                {
                    more = true;
                    break;
                }

                continue;
            }

            var cells = ReadRow(row, cellReader);
            if (cells.Any(c => c.Length > 0))
            {
                rows.Add(cells);
                chars += cells.Sum(c => c.Length + 3);
            }
        }

        return rows;
    }

    private static string[] ReadRow(Row row, CellReader cellReader)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var column = ColumnIndex(cell.CellReference?.Value) ?? cells.Count;
            if (column >= MarkdownTable.MaxColumns)
            {
                continue;
            }

            while (cells.Count <= column)
            {
                cells.Add("");
            }

            cells[column] = cellReader.Text(cell);
        }

        return [.. cells];
    }

    /// <summary>Cell values as text, with the workbook's shared strings, date styles and date system.</summary>
    private sealed class CellReader(SharedStrings strings, IReadOnlySet<uint> dateStyles, double dateOffset)
    {
        public string Text(Cell cell)
        {
            var value = cell.CellValue?.Text ?? "";
            var type = cell.DataType?.Value;
            if (type == CellValues.SharedString)
            {
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? strings.Get(i) : "";
            }

            if (type == CellValues.InlineString)
            {
                return cell.InlineString?.InnerText ?? "";
            }

            if (type == CellValues.Boolean)
            {
                return value == "1" ? "TRUE" : "FALSE";
            }

            if (type is null || type == CellValues.Number)
            {
                return FormatNumber(value, cell.StyleIndex?.Value is { } style && dateStyles.Contains(style));
            }

            return value;
        }

        private string FormatNumber(string value, bool isDate)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return value;
            }

            if (isDate && number >= 0 && number + dateOffset < 2958466)
            {
                // A serial below 1 is a time of day with no date (h:mm and the other time-only formats).
                if (number < 1)
                {
                    return DateTime.FromOADate(number).ToString("HH:mm", CultureInfo.InvariantCulture);
                }

                var date = DateTime.FromOADate(number + dateOffset);
                return date.TimeOfDay == TimeSpan.Zero
                    ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            return number.ToString("G15", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Zero-based column of a reference such as <c>C7</c>; null when there is none.</summary>
    internal static int? ColumnIndex(string? reference)
    {
        var index = 0;
        var letters = 0;
        foreach (var c in reference ?? "")
        {
            if (!char.IsAsciiLetter(c) || letters == 3)
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
            letters++;
        }

        return letters == 0 ? null : index - 1;
    }

    /// <summary>
    /// The shared-string table, streamed forward only as far as the highest index asked for, so a large table costs
    /// only the strings the shown rows use. Each string is cut one character past the character limit.
    /// </summary>
    private sealed class SharedStrings(SharedStringTablePart? part, int maxChars, CancellationToken ct) : IDisposable
    {
        private readonly OpenXmlReader? reader = part is null ? null : OpenXmlReader.Create(part);
        private readonly List<string> strings = [];

        public string Get(int index)
        {
            while (strings.Count <= index && reader is not null && reader.Read())
            {
                if (reader.ElementType == typeof(SharedStringItem) && reader.IsStartElement)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = (SharedStringItem)reader.LoadCurrentElement()!;
                    var text = string.Concat(item.Descendants<Text>().Where(t => t.Parent is not PhoneticRun).Select(t => t.Text));
                    strings.Add(text.Length > maxChars ? text[..(maxChars + 1)] : text);
                }
            }

            return index < strings.Count ? strings[index] : "";
        }

        public void Dispose() => reader?.Dispose();
    }

    /// <summary>Indexes of the cell formats that show a date: built-in date formats or custom codes with a day or year.</summary>
    private static HashSet<uint> DateStyles(Stylesheet? stylesheet)
    {
        var custom = stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.NumberFormatId?.Value is not null && IsDateCode(f.FormatCode?.Value))
            .Select(f => f.NumberFormatId!.Value)
            .ToHashSet() ?? [];
        var styles = new HashSet<uint>();
        var index = 0u;
        foreach (var format in stylesheet?.CellFormats?.Elements<CellFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } id && (BuiltInDateFormats.Contains(id) || custom.Contains(id)))
            {
                styles.Add(index);
            }

            index++;
        }

        return styles;
    }

    private static bool IsDateCode(string? code)
    {
        if (code is null)
        {
            return false;
        }

        var plain = new StringBuilder();
        var skip = '\0';
        foreach (var c in code)
        {
            if (skip != '\0')
            {
                skip = c == skip ? '\0' : skip;
            }
            else if (c is '"')
            {
                skip = '"';
            }
            else if (c is '[')
            {
                skip = ']';
            }
            else
            {
                plain.Append(char.ToLowerInvariant(c));
            }
        }

        var text = plain.ToString();
        return text.Contains('y') || text.Contains('d');
    }
}
