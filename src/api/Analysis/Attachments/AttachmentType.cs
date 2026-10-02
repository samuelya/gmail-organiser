using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// What an attachment is, resolved by <see cref="AttachmentTypeResolver"/>; settings enable conversion per type. The
/// snake_case names (<c>pdf</c>, <c>word_document</c>, …) are the API and settings-document contract: keep them stable.
/// </summary>
[JsonConverter(typeof(AttachmentTypeJsonConverter))]
public enum AttachmentType
{
    Pdf,
    Image,
    Spreadsheet,
    Csv,
    WordDocument,
    Presentation,
    PlainText,
    Archive,
    Other,
}

/// <summary>Serialises <see cref="AttachmentType"/> as its snake_case name (API and settings document).</summary>
public sealed class AttachmentTypeJsonConverter()
    : JsonStringEnumConverter<AttachmentType>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
{
    private static readonly Dictionary<string, AttachmentType> ByName = Enum.GetValues<AttachmentType>()
        .ToDictionary(t => JsonNamingPolicy.SnakeCaseLower.ConvertName(t.ToString()), StringComparer.Ordinal);

    /// <summary>Parses an exact snake_case name; integers and other spellings are not types.</summary>
    public static bool TryParse(string? name, [NotNullWhen(true)] out AttachmentType? type)
    {
        type = name is not null && ByName.TryGetValue(name, out var found) ? found : null;
        return type is not null;
    }
}
