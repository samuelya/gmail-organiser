using System.Text;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>Renders rows as a markdown table; the first row is the header. Shared by the CSV and office converters.</summary>
public static class MarkdownTable
{
    /// <summary>Columns beyond this are dropped, so a cell far to the right can't blow up every row.</summary>
    public const int MaxColumns = 50;

    /// <summary>
    /// Appends <paramref name="rows"/> as a table: <c>|</c> escaped, line breaks in a cell flattened to a space, empty
    /// cells left empty, short rows padded and trailing empty columns dropped. Nothing is appended for no rows.
    /// </summary>
    public static void Append(StringBuilder markdown, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columns = Math.Min(MaxColumns, rows.Count == 0 ? 0 : rows.Max(LastNonEmpty) + 1);
        if (columns == 0)
        {
            return;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            AppendRow(markdown, rows[i], columns);
            if (i == 0)
            {
                markdown.Append('|');
                markdown.Insert(markdown.Length, " --- |", columns);
                markdown.Append('\n');
            }
        }
    }

    private static int LastNonEmpty(IReadOnlyList<string> row)
    {
        for (var i = row.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(row[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static void AppendRow(StringBuilder markdown, IReadOnlyList<string> row, int columns)
    {
        markdown.Append('|');
        for (var c = 0; c < columns; c++)
        {
            var cell = c < row.Count ? Escape(row[c]) : "";
            markdown.Append(cell.Length == 0 ? " |" : $" {cell} |");
        }

        markdown.Append('\n');
    }

    private static string Escape(string cell) =>
        cell.Trim().Replace("|", "\\|").ReplaceLineEndings(" ");
}
