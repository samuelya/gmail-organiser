using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmailOrganiser.Settings;

/// <summary>How an analysis batch groups similar messages so fewer LLM calls than emails are made.</summary>
[JsonConverter(typeof(AnalysisGroupingModeJsonConverter))]
public enum AnalysisGroupingMode
{
    Off,
    SenderSubject,
    Auto,
}

/// <summary>Serialises <see cref="AnalysisGroupingMode"/> as <c>off | sender_subject | auto</c> (API and settings document).</summary>
public sealed class AnalysisGroupingModeJsonConverter()
    : JsonStringEnumConverter<AnalysisGroupingMode>(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);
