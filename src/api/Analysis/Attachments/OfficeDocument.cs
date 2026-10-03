using System.Xml;
using DocumentFormat.OpenXml.Packaging;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>Shared Open XML settings for the docx, xlsx and pptx converters.</summary>
internal static class OfficeDocument
{
    /// <summary>
    /// A part that expands past this many characters throws instead of filling memory (zip bomb). Generous: a 10 MB
    /// attachment rarely expands beyond a few hundred MB, and worksheets are read row by row and abandoned early.
    /// </summary>
    public const long MaxCharactersInPart = 64L * 1024 * 1024;

    /// <summary>
    /// Elements nest no deeper than this. Real documents stay far below it (a table in a text box in a table is
    /// about 30 levels); thousands of levels overflow the stack.
    /// </summary>
    public const int MaxDepth = 128;

    public static OpenSettings ReadOnly => new() { MaxCharactersInPart = MaxCharactersInPart, AutoSave = false };

    /// <summary>
    /// Throws when the XML of <paramref name="part"/> nests deeper than <see cref="MaxDepth"/>. The SDK builds
    /// elements recursively, and a stack overflow kills the process where no catch can stop it, so this runs before
    /// anything loads an element of the part. The scan streams and stops quietly at malformed XML: the SDK can't read
    /// past that point either, and a streamed part may never get there.
    /// </summary>
    public static void CheckDepth(OpenXmlPart? part, CancellationToken ct)
    {
        if (part is null)
        {
            return;
        }

        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharactersInPart,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        try
        {
            for (var nodes = 1; reader.Read(); nodes++)
            {
                if (reader.Depth > MaxDepth)
                {
                    throw new InvalidDataException($"The part {part.Uri} nests deeper than {MaxDepth} levels.");
                }

                if (nodes % 4096 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
            }
        }
        catch (XmlException)
        {
        }
    }

    /// <summary>
    /// Whether <paramref name="content"/> is plain text rather than a zip package or a legacy binary file: no
    /// <c>PK</c> signature and no NUL byte in the first 4 KB. The position is left unchanged; an unseekable stream
    /// counts as not text.
    /// </summary>
    public static bool IsPlainText(Stream content)
    {
        if (!content.CanSeek)
        {
            return false;
        }

        var start = content.Position;
        var head = new byte[4096];
        var read = content.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        content.Position = start;
        return read > 0 && !(read >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K') && Array.IndexOf(head, (byte)0, 0, read) < 0;
    }
}
