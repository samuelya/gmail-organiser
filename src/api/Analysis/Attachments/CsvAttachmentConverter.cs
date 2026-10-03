using System.Text;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// A CSV file as one markdown table of its first <c>maxSheetRows</c> rows, the first row as the header. The delimiter
/// is sniffed from the first record (<c>,</c>, <c>;</c> or tab, comma when none occurs); quoted fields follow RFC 4180.
/// UTF-8 with or without a BOM. The file is read character by character and reading stops, inside a field too, once
/// the text exceeds <see cref="ConversionLimits.MaxChars"/>, so a stray quote can't pull the whole file into one cell.
/// </summary>
/// <param name="parseTimeout">Defaults to 30 s; a test seam.</param>
public sealed class CsvAttachmentConverter(int maxSheetRows = AttachmentOptions.DefaultMaxSheetRows, TimeSpan? parseTimeout = null)
    : IAttachmentConverter
{
    /// <summary>The first record is sniffed within this many characters.</summary>
    private const int SniffChars = 64 * 1024;

    private static readonly char[] Delimiters = [',', ';', '\t'];

    private readonly int maxSheetRows = maxSheetRows > 0 ? maxSheetRows : throw new ArgumentOutOfRangeException(nameof(maxSheetRows));
    private readonly TimeSpan parseTimeout = parseTimeout ?? ParseTimeout.Default;

    public bool CanConvert(AttachmentType type) => type == AttachmentType.Csv;

    public async Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var markdown = await ParseTimeout.RunAsync(t => Read(content, maxSheetRows, limits.MaxChars, t), parseTimeout, "CSV", ct);
        return new ConvertedAttachment(attachment.Filename, AttachmentType.Csv, markdown, false);
    }

    /// <summary>The table for <paramref name="content"/>; also the spreadsheet converter's fallback for plain-text content.</summary>
    internal static string Read(Stream content, int maxRows, int maxChars, CancellationToken ct)
    {
        using var reader = new StreamReader(content, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var head = new char[Math.Min(SniffChars, maxChars + 1)];
        var source = new CharSource(new string(head, 0, reader.ReadBlock(head)), reader);
        var rows = ReadRows(source, SniffDelimiter(source.Head), maxRows, maxChars, ct, out var more);
        var markdown = new StringBuilder();
        MarkdownTable.Append(markdown, rows);
        if (more)
        {
            markdown.Append('\n').Append(MarkdownTable.RowCapNote(maxRows));
        }

        return markdown.ToString().TrimEnd();
    }

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
    /// Up to <paramref name="maxRows"/> records, fewer once their text exceeds <paramref name="maxChars"/> (the record
    /// being read is cut there); blank lines are skipped. <paramref name="more"/> is set when rows were left out for
    /// the row cap.
    /// </summary>
    private static List<IReadOnlyList<string>> ReadRows(CharSource source, char delimiter, int maxRows, int maxChars, CancellationToken ct, out bool more)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var chars = 0;
        var pending = 0;
        more = false;

        for (var c = source.Read(); c >= 0; c = source.Read())
        {
            if ((++pending & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (quoted)
            {
                if (c != '"')
                {
                    field.Append((char)c);
                }
                else if (source.Peek() == '"')
                {
                    field.Append('"');
                    source.Read();
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
                chars += field.Length + 3;
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && source.Peek() == '\n')
                {
                    source.Read();
                }

                if (!EndRow(rows, row, field, maxRows, maxChars, ref chars))
                {
                    more = rows.Count >= maxRows && source.HasContent();
                    return rows;
                }

                ct.ThrowIfCancellationRequested();
            }
            else
            {
                field.Append((char)c);
            }

            if (chars + field.Length > maxChars)
            {
                // A huge field or row: keep what fits and stop; the service truncates.
                EndRow(rows, row, field, maxRows, maxChars, ref chars);
                return rows;
            }
        }

        EndRow(rows, row, field, maxRows, maxChars, ref chars);
        return rows;
    }

    /// <param name="chars">The characters of the rows so far plus the finished fields of this row.</param>
    /// <returns>Whether to keep reading.</returns>
    private static bool EndRow(List<IReadOnlyList<string>> rows, List<string> row, StringBuilder field, int maxRows, int maxChars, ref int chars)
    {
        chars += field.Length + 3;
        row.Add(field.ToString());
        field.Clear();
        if (row.Count > 1 || row[0].Length > 0)
        {
            rows.Add([.. row]);
        }
        else
        {
            chars -= 3;
        }

        row.Clear();
        return rows.Count < maxRows && chars <= maxChars;
    }

    /// <summary>The sniffed head of the file followed by the rest of the reader.</summary>
    private sealed class CharSource(string head, TextReader rest)
    {
        private int position;

        public string Head => head;

        public int Peek() => position < head.Length ? head[position] : rest.Peek();

        public int Read() => position < head.Length ? head[position++] : rest.Read();

        /// <summary>Whether anything but white space is left; reads up to the first such character.</summary>
        public bool HasContent()
        {
            for (var c = Read(); c >= 0; c = Read())
            {
                if (!char.IsWhiteSpace((char)c))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
