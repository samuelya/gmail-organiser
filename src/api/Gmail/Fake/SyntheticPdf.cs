using System.Globalization;
using System.Text;

namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// Builds a minimal valid PDF (header, catalog, pages with one text object each, xref, trailer) from synthetic text,
/// so the fake mailbox carries attachments without binaries in git. ASCII only: other characters become <c>?</c>.
/// </summary>
public static class SyntheticPdf
{
    private const int PaddingLineLength = 64;

    /// <param name="minimumSize">Pads with PDF comment lines until the file is at least this many bytes.</param>
    public static byte[] Create(string title, string bodyText, int minimumSize = 0)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(bodyText);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumSize);

        string[] pages = [$"BT /F1 18 Tf 72 720 Td ({Escape(title)}) Tj /F1 12 Tf 0 -28 Td ({Escape(bodyText)}) Tj ET"];
        var unpadded = Build(pages, paddingLines: 0);
        var shortfall = minimumSize - unpadded.Length;
        return shortfall <= 0
            ? unpadded
            : Build(pages, paddingLines: (shortfall + PaddingLineLength - 1) / PaddingLineLength);
    }

    /// <summary>One page per entry with that line of text; an empty entry is a page without a text layer, like a scan.</summary>
    public static byte[] CreatePages(IReadOnlyList<string> pageTexts)
    {
        ArgumentNullException.ThrowIfNull(pageTexts);
        ArgumentOutOfRangeException.ThrowIfZero(pageTexts.Count);
        return Build([.. pageTexts.Select(t => t.Length == 0 ? "" : $"BT /F1 12 Tf 72 720 Td ({Escape(t)}) Tj ET")], paddingLines: 0);
    }

    /// <summary>Catalog, pages, then a page and a content stream per page, and one shared font.</summary>
    private static byte[] Build(IReadOnlyList<string> pageContents, int paddingLines)
    {
        var fontObject = 3 + (2 * pageContents.Count);
        var kids = string.Join(' ', Enumerable.Range(0, pageContents.Count).Select(i => $"{3 + (2 * i)} 0 R"));
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pageContents.Count} >>",
        };
        for (var i = 0; i < pageContents.Count; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {4 + (2 * i)} 0 R /Resources << /Font << /F1 {fontObject} 0 R >> >> >>");
            objects.Add($"<< /Length {pageContents[i].Length} >>\nstream\n{pageContents[i]}\nendstream");
        }

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var pdf = new StringBuilder("%PDF-1.4\n");
        for (var i = 0; i < paddingLines; i++)
        {
            pdf.Append('%').Append('x', PaddingLineLength - 2).Append('\n');
        }

        var offsets = new int[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            escaped.Append(c switch
            {
                '\\' or '(' or ')' => $"\\{c}",
                < ' ' or > '~' => "?",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }
}
