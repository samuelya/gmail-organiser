using System.Text;
using System.Text.Json;
using GmailOrganiser.Llm;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// Asks the vision model chosen in Settings to describe one image and transcribe its text, with the versioned prompt
/// <c>image-vision-v1.md</c>. The answer must be JSON <c>{"description": string, "text": string}</c>; anything else is an
/// <see cref="InvalidDataException"/>, which the conversion records as a failed attachment.
/// </summary>
public sealed class OllamaVisionClient(ILlmClientFactory llm) : IVisionClient
{
    public const string PromptVersion = "image-vision-v1";

    private const string ResourceName = "GmailOrganiser.Analysis.Attachments.image-vision-v1.md";

    public static string Prompt { get; } = LoadPrompt();

    public async Task<ImageText> ReadAsync(ReadOnlyMemory<byte> image, ImageReading reading, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var model = reading.VisionModel ?? throw new InvalidOperationException("No vision model is selected.");
        var mediaType = ImageTextReader.MediaType(image.Span) ?? throw new InvalidDataException("The image format is not recognised.");

        using var client = llm.CreateChatClient(OllamaHttp.Parse(reading.OllamaBaseUrl), model);
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, [new TextContent(Prompt), new DataContent(image, mediaType)])],
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0 },
            ct);
        return Parse(response.Text);
    }

    /// <summary>The model's answer as <see cref="ImageText"/>; the description is collapsed to one line.</summary>
    /// <exception cref="InvalidDataException">Not a JSON object with a non-empty string description and a string text.</exception>
    public static ImageText Parse(string answer)
    {
        try
        {
            using var json = JsonDocument.Parse(answer);
            if (json.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                && string.Join(' ', description.GetString()!.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    is { Length: > 0 } line
                && root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return new ImageText(line, text.GetString()!.Trim());
            }
        }
        catch (JsonException)
        {
            // Falls through: the message of a JsonException can quote the answer.
        }

        throw new InvalidDataException("The vision model's answer is not the expected JSON.");
    }

    private static string LoadPrompt()
    {
        using var stream = typeof(OllamaVisionClient).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded prompt '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
