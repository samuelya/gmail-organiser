using System.Net.Http.Headers;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// Resolves an <see cref="AttachmentType"/> from the MIME type first and the filename extension second: mail clients
/// often send <c>application/octet-stream</c>, and the extension then decides. Executables and anything unknown are
/// <see cref="AttachmentType.Other"/>.
/// </summary>
public static class AttachmentTypeResolver
{
    private static readonly Dictionary<string, AttachmentType> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = AttachmentType.Pdf,
        ["application/x-pdf"] = AttachmentType.Pdf,
        ["text/csv"] = AttachmentType.Csv,
        ["application/csv"] = AttachmentType.Csv,
        ["text/comma-separated-values"] = AttachmentType.Csv,
        ["application/vnd.ms-excel"] = AttachmentType.Spreadsheet,
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = AttachmentType.Spreadsheet,
        ["application/vnd.oasis.opendocument.spreadsheet"] = AttachmentType.Spreadsheet,
        ["application/msword"] = AttachmentType.WordDocument,
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = AttachmentType.WordDocument,
        ["application/vnd.oasis.opendocument.text"] = AttachmentType.WordDocument,
        ["application/rtf"] = AttachmentType.WordDocument,
        ["text/rtf"] = AttachmentType.WordDocument,
        ["application/vnd.ms-powerpoint"] = AttachmentType.Presentation,
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = AttachmentType.Presentation,
        ["application/vnd.oasis.opendocument.presentation"] = AttachmentType.Presentation,
        ["text/plain"] = AttachmentType.PlainText,
        ["text/markdown"] = AttachmentType.PlainText,
        ["application/zip"] = AttachmentType.Archive,
        ["application/x-zip-compressed"] = AttachmentType.Archive,
        ["application/x-7z-compressed"] = AttachmentType.Archive,
        ["application/vnd.rar"] = AttachmentType.Archive,
        ["application/x-rar-compressed"] = AttachmentType.Archive,
        ["application/x-tar"] = AttachmentType.Archive,
        ["application/gzip"] = AttachmentType.Archive,
        ["application/x-gzip"] = AttachmentType.Archive,
    };

    private static readonly Dictionary<string, AttachmentType> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = AttachmentType.Pdf,
        [".png"] = AttachmentType.Image,
        [".jpg"] = AttachmentType.Image,
        [".jpeg"] = AttachmentType.Image,
        [".gif"] = AttachmentType.Image,
        [".webp"] = AttachmentType.Image,
        [".bmp"] = AttachmentType.Image,
        [".tif"] = AttachmentType.Image,
        [".tiff"] = AttachmentType.Image,
        [".heic"] = AttachmentType.Image,
        [".csv"] = AttachmentType.Csv,
        [".xls"] = AttachmentType.Spreadsheet,
        [".xlsx"] = AttachmentType.Spreadsheet,
        [".ods"] = AttachmentType.Spreadsheet,
        [".doc"] = AttachmentType.WordDocument,
        [".docx"] = AttachmentType.WordDocument,
        [".odt"] = AttachmentType.WordDocument,
        [".rtf"] = AttachmentType.WordDocument,
        [".ppt"] = AttachmentType.Presentation,
        [".pptx"] = AttachmentType.Presentation,
        [".odp"] = AttachmentType.Presentation,
        [".txt"] = AttachmentType.PlainText,
        [".md"] = AttachmentType.PlainText,
        [".zip"] = AttachmentType.Archive,
        [".7z"] = AttachmentType.Archive,
        [".rar"] = AttachmentType.Archive,
        [".tar"] = AttachmentType.Archive,
        [".gz"] = AttachmentType.Archive,
        [".tgz"] = AttachmentType.Archive,
    };

    public static AttachmentType Resolve(string? mimeType, string? filename)
    {
        if (FromMimeType(mimeType) is { } byMime)
        {
            return byMime;
        }

        var extension = Path.GetExtension(filename ?? "");
        return Extensions.TryGetValue(extension, out var byExtension) ? byExtension : AttachmentType.Other;
    }

    /// <summary>The type the MIME type names; null when it is missing, malformed or generic.</summary>
    private static AttachmentType? FromMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType) || !MediaTypeHeaderValue.TryParse(mimeType, out var parsed) || parsed.MediaType is not { } media)
        {
            return null;
        }

        if (MimeTypes.TryGetValue(media, out var type))
        {
            return type;
        }

        return media.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? AttachmentType.Image : null;
    }
}
