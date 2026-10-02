using System.Globalization;
using System.Text;

namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// Builds a minimal valid one-page PDF (header, catalog, page, one text object, xref, trailer) from synthetic text,
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

        var unpadded = Build(title, bodyText, paddingLines: 0);
        var shortfall = minimumSize - unpadded.Length;
        return shortfall <= 0
            ? unpadded
            : Build(title, bodyText, paddingLines: (shortfall + PaddingLineLength - 1) / PaddingLineLength);
    }

    private static byte[] Build(string title, string bodyText, int paddingLines)
    {
        var content = $"BT /F1 18 Tf 72 720 Td ({Escape(title)}) Tj /F1 12 Tf 0 -28 Td ({Escape(bodyText)}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        for (var i = 0; i < paddingLines; i++)
        {
            pdf.Append('%').Append('x', PaddingLineLength - 2).Append('\n');
        }

        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
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
