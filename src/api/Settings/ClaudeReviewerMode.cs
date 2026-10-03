using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailOrganiser.Settings;

/// <summary>Who gives the Claude second opinion (DESIGN §6.7); <see cref="Off"/> keeps the feature hidden.</summary>
[JsonConverter(typeof(ClaudeReviewerModeJsonConverter))]
public enum ClaudeReviewerMode
{
    Off,
    HeadlessClaudeCode,
    ClaudeDesktop,
}

/// <summary>Serialises <see cref="ClaudeReviewerMode"/> as <c>off | headless_claude_code | claude_desktop</c> (API and settings document).</summary>
public sealed class ClaudeReviewerModeJsonConverter()
    : JsonStringEnumConverter<ClaudeReviewerMode>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);
