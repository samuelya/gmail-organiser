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

    public static OpenSettings ReadOnly => new() { MaxCharactersInPart = MaxCharactersInPart, AutoSave = false };

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
