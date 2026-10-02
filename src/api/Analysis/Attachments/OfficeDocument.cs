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
}
