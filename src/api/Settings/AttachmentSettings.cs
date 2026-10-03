using System.Text.Json;
using System.Text.Json.Serialization;
using GmailOrganiser.Analysis.Attachments;

namespace GmailOrganiser.Settings;

/// <summary>
/// Which attachment types the analysis may read, and the limits (block <c>attachments</c> of <see cref="AppSettings"/>).
/// <see cref="Enabled"/> off means the analysis prompt gets no attachment block at all. Ranges are in
/// <see cref="SettingsValidation"/>.
/// </summary>
public sealed record AttachmentSettings
{
    public const int DefaultMaxBytes = 10 * 1024 * 1024;
    public const int DefaultMaxImageBytes = 10 * 1024 * 1024;
    public const int DefaultMaxChars = 4000;
    public const int DefaultMaxPerMessage = 5;

    /// <summary>Master switch; on by default (owner decision on #68).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// One entry per <see cref="AttachmentType"/>, in enum order. On read, unknown type names are dropped and missing
    /// types take their default; <see cref="AttachmentType.Archive"/> is always off.
    /// </summary>
    [JsonConverter(typeof(AttachmentTypeSettingsJsonConverter))]
    public IReadOnlyList<AttachmentTypeSetting> Types { get; init; } = DefaultTypes;

    public int MaxBytes { get; init; } = DefaultMaxBytes;
    public int MaxImageBytes { get; init; } = DefaultMaxImageBytes;
    public int MaxChars { get; init; } = DefaultMaxChars;
    public int MaxPerMessage { get; init; } = DefaultMaxPerMessage;

    /// <summary>Images and scanned PDF pages: local OCR by default (owner decision on #68), or the vision model.</summary>
    public ImageMode ImageMode { get; init; } = ImageMode.Ocr;

    /// <summary>Archives and anything unrecognised are off by default; archives can never be enabled.</summary>
    public static bool IsEnabledByDefault(AttachmentType type) => type is not (AttachmentType.Archive or AttachmentType.Other);

    public static IReadOnlyList<AttachmentTypeSetting> DefaultTypes { get; } =
        [.. Enum.GetValues<AttachmentType>().Select(t => new AttachmentTypeSetting(t, IsEnabledByDefault(t)))];

    /// <summary>
    /// <paramref name="saved"/> over the defaults: one entry per type in enum order, the first saved entry for a type
    /// wins, and <see cref="AttachmentType.Archive"/> stays off whatever was saved.
    /// </summary>
    public static IReadOnlyList<AttachmentTypeSetting> WithDefaults(IEnumerable<AttachmentTypeSetting> saved)
    {
        var byType = new Dictionary<AttachmentType, bool>();
        foreach (var setting in saved)
        {
            byType.TryAdd(setting.Type, setting.Enabled);
        }

        return [.. DefaultTypes.Select(d => d with
        {
            Enabled = d.Type != AttachmentType.Archive && byType.GetValueOrDefault(d.Type, d.Enabled),
        })];
    }

    /// <summary>
    /// The types the converters may read: none when the master switch is off, never archives, and no images in
    /// <see cref="ImageMode.Vision"/> without <paramref name="visionModel"/> (they are skipped as disabled, never an error).
    /// </summary>
    /// <param name="visionModel"><see cref="AppSettings.VisionModel"/>.</param>
    public IReadOnlySet<AttachmentType> EnabledTypes(string? visionModel = null) => Enabled
        ? Types.Where(t => t.Enabled && t.Type != AttachmentType.Archive && (t.Type != AttachmentType.Image || ImagesReadable(visionModel)))
            .Select(t => t.Type).ToHashSet()
        : new HashSet<AttachmentType>();

    /// <summary>The limits; <see cref="ConversionLimits.Images"/> is set exactly when <see cref="EnabledTypes"/> has images.</summary>
    /// <param name="visionModel"><see cref="AppSettings.VisionModel"/>.</param>
    /// <param name="ollamaBaseUrl"><see cref="AppSettings.OllamaBaseUrl"/>.</param>
    public ConversionLimits ToLimits(string? visionModel = null, string ollamaBaseUrl = AppSettings.FallbackOllamaBaseUrl) =>
        new(MaxBytes, MaxImageBytes, MaxChars, MaxPerMessage,
            EnabledTypes(visionModel).Contains(AttachmentType.Image) ? new ImageReading(ImageMode, visionModel, ollamaBaseUrl) : null);

    /// <summary>OCR needs nothing more; vision needs a model.</summary>
    public bool ImagesReadable(string? visionModel) => ImageMode == ImageMode.Ocr || visionModel is not null;
}

public sealed record AttachmentTypeSetting(AttachmentType Type, bool Enabled);

/// <summary>
/// Reads the saved type list leniently (entries with an unknown or unreadable type are skipped, then
/// <see cref="AttachmentSettings.WithDefaults"/>), so a renamed or removed type never discards the user's other choices.
/// </summary>
public sealed class AttachmentTypeSettingsJsonConverter : JsonConverter<IReadOnlyList<AttachmentTypeSetting>>
{
    public override IReadOnlyList<AttachmentTypeSetting> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array of attachment type settings.");
        }

        var saved = new List<AttachmentTypeSetting>();
        foreach (var entry in JsonElement.ParseValue(ref reader).EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("type", out var typeName) && typeName.ValueKind == JsonValueKind.String
                && AttachmentTypeJsonConverter.TryParse(typeName.GetString(), out var type)
                && entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                saved.Add(new AttachmentTypeSetting(type.Value, enabled.GetBoolean()));
            }
        }

        return AttachmentSettings.WithDefaults(saved);
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<AttachmentTypeSetting> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var setting in value)
        {
            JsonSerializer.Serialize(writer, setting, options);
        }

        writer.WriteEndArray();
    }
}
