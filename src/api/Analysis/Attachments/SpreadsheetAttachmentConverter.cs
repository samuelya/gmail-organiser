using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// An xlsx workbook as markdown: per worksheet a <c>## name</c> heading and a table of its first <c>maxSheetRows</c>
/// non-empty rows (Open XML SDK, MIT). Worksheets are read row by row, so a large sheet costs only the rows shown.
/// Numbers are invariant, date-formatted cells become ISO dates. Legacy xls and ods aren't Open XML and throw, which the
/// caller records as failed. Reading stops once the text exceeds <see cref="ConversionLimits.MaxChars"/>.
/// </summary>
/// <param name="parseTimeout">Defaults to 30 s; a test seam.</param>
public sealed class SpreadsheetAttachmentConverter(int maxSheetRows = AttachmentOptions.DefaultMaxSheetRows, TimeSpan? parseTimeout = null)
    : IAttachmentConverter
{
    /// <summary>Built-in number formats that show a date (ECMA-376 18.8.30).</summary>
    private static readonly HashSet<uint> BuiltInDateFormats = [14, 15, 16, 17, 22];

    private readonly int maxSheetRows = maxSheetRows > 0 ? maxSheetRows : throw new ArgumentOutOfRangeException(nameof(maxSheetRows));
    private readonly TimeSpan parseTimeout = parseTimeout ?? ParseTimeout.Default;

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Spreadsheet;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var markdown = await ParseTimeout.RunAsync(t => Read(content, limits.MaxChars, t), parseTimeout, "spreadsheet", ct);
        return new ConvertedAttachment(attachment.Filename, AttachmentType.Spreadsheet, markdown, false);
    }

    private string Read(Stream content, int maxChars, CancellationToken ct)
    {
        using var document = SpreadsheetDocument.Open(content, false, OfficeDocument.ReadOnly);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("The workbook has no workbook part.");
        var strings = ReadSharedStrings(workbook.SharedStringTablePart, ct);
        var dateStyles = DateStyles(workbook.WorkbookStylesPart?.Stylesheet);
        var markdown = new StringBuilder();

        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
            {
                continue;
            }

            var rows = ReadRows(part, strings, dateStyles, maxChars - markdown.Length, ct, out var more);
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
                markdown.Append('\n').Append(CsvAttachmentConverter.RowCapNote(maxSheetRows)).Append('\n');
            }

            if (markdown.Length > maxChars)
            {
                break;
            }
        }

        return markdown.ToString().TrimEnd();
    }

    private List<IReadOnlyList<string>> ReadRows(
        WorksheetPart part, IReadOnlyList<string> strings, IReadOnlySet<uint> dateStyles, int maxChars, CancellationToken ct, out bool more)
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

            var cells = ReadRow(row, strings, dateStyles);
            if (cells.Any(c => c.Length > 0))
            {
                rows.Add(cells);
                chars += cells.Sum(c => c.Length + 3);
            }
        }

        return rows;
    }

    private static string[] ReadRow(Row row, IReadOnlyList<string> strings, IReadOnlySet<uint> dateStyles)
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

            cells[column] = CellText(cell, strings, dateStyles);
        }

        return [.. cells];
    }

    private static string CellText(Cell cell, IReadOnlyList<string> strings, IReadOnlySet<uint> dateStyles)
    {
        var value = cell.CellValue?.Text ?? "";
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < strings.Count ? strings[i] : "";
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

    private static string FormatNumber(string value, bool isDate)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return value;
        }

        if (isDate && number is >= 0 and < 2958466)
        {
            var date = DateTime.FromOADate(number);
            return date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        return number.ToString("G15", CultureInfo.InvariantCulture);
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

    private static List<string> ReadSharedStrings(SharedStringTablePart? part, CancellationToken ct)
    {
        var strings = new List<string>();
        if (part is null)
        {
            return strings;
        }

        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType == typeof(SharedStringItem) && reader.IsStartElement)
            {
                ct.ThrowIfCancellationRequested();
                var item = (SharedStringItem)reader.LoadCurrentElement()!;
                strings.Add(string.Concat(item.Descendants<Text>().Where(t => t.Parent is not PhoneticRun).Select(t => t.Text)));
            }
        }

        return strings;
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
