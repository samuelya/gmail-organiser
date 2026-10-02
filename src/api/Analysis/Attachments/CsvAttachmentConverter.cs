using System.Text;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// A CSV file as one markdown table of its first <c>maxSheetRows</c> rows, the first row as the header. The delimiter
/// is sniffed from the first record (<c>,</c>, <c>;</c> or tab, comma when none occurs); quoted fields follow RFC 4180.
/// UTF-8 with or without a BOM. Reading stops once the table exceeds <see cref="ConversionLimits.MaxChars"/>.
/// </summary>
public sealed class CsvAttachmentConverter(int maxSheetRows = AttachmentOptions.DefaultMaxSheetRows) : IAttachmentConverter
{
    private static readonly char[] Delimiters = [',', ';', '\t'];

    private readonly int maxSheetRows = maxSheetRows > 0 ? maxSheetRows : throw new ArgumentOutOfRangeException(nameof(maxSheetRows));

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Csv;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        using var reader = new StreamReader(content, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        var markdown = new StringBuilder();
        var rows = ReadRows(text, SniffDelimiter(text), maxSheetRows, limits.MaxChars, out var more);
        MarkdownTable.Append(markdown, rows);
        if (more)
        {
            markdown.Append('\n').Append(RowCapNote(maxSheetRows));
        }

        return new ConvertedAttachment(attachment.Filename, AttachmentType.Csv, markdown.ToString().TrimEnd(), false);
    }

    internal static string RowCapNote(int maxRows) => $"_Only the first {maxRows} rows are shown._";

    /// <summary>The candidate that occurs most often outside quotes in the first record; comma on a tie or none.</summary>
    internal static char SniffDelimiter(string text)
    {
        var counts = new int[Delimiters.Length];
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\r' or '\n')
            {
                break;
            }
            else if (!quoted && Array.IndexOf(Delimiters, c) is var i and >= 0)
            {
                counts[i]++;
            }
        }

        var best = 0;
        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] > counts[best])
            {
                best = i;
            }
        }

        return Delimiters[best];
    }

    /// <summary>
    /// Up to <paramref name="maxRows"/> records, fewer once their text exceeds <paramref name="maxChars"/>; blank
    /// lines are skipped. <paramref name="more"/> is set when rows were left out for the row cap.
    /// </summary>
    internal static List<IReadOnlyList<string>> ReadRows(string text, char delimiter, int maxRows, int maxChars, out bool more)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var chars = 0;
        more = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                if (!EndRow(rows, row, field, maxRows, maxChars, ref chars))
                {
                    more = rows.Count >= maxRows && HasContent(text, i + 1);
                    return rows;
                }
            }
            else
            {
                field.Append(c);
            }
        }

        EndRow(rows, row, field, maxRows, maxChars, ref chars);
        return rows;
    }

    /// <returns>Whether to keep reading.</returns>
    private static bool EndRow(List<IReadOnlyList<string>> rows, List<string> row, StringBuilder field, int maxRows, int maxChars, ref int chars)
    {
        row.Add(field.ToString());
        field.Clear();
        if (row.Count > 1 || row[0].Length > 0)
        {
            rows.Add([.. row]);
            chars += row.Sum(f => f.Length + 3);
        }

        row.Clear();
        return rows.Count < maxRows && chars <= maxChars;
    }

    private static bool HasContent(string text, int from) => text.AsSpan(from).Trim().Length > 0;
}
