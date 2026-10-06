using System.Globalization;
using System.Text;
using System.Text.Json;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Rules.Review;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Rules.Prompts;

/// <summary>One finding as the summary prompt shows it; <paramref name="Create"/> is the filter its fix creates.</summary>
public sealed record SummaryFinding(
    FilterFindingKind Kind,
    FilterFindingStatus Status,
    IReadOnlyList<string> FilterIds,
    string Description,
    FilterFixKind FixKind,
    IReadOnlyList<string> DeleteFilterIds,
    FilterDto? Create);

/// <summary>
/// Turns a filter review's findings and the filters they refer to into the chat messages for the summary (DESIGN §6.5),
/// and cleans the model's answer. Only criteria, label names and the checks' own text reach the prompt. No I/O.
/// </summary>
public static class RulesSummaryPromptBuilder
{
    public const string Version = "rules-summary-v1";
    public const int MaxFilters = 200;
    public const int MaxFindings = 100;
    public const int MaxSummaryLength = 2000;
    public const string FiltersPlaceholder = "{{filters}}";
    public const string FindingsPlaceholder = "{{findings}}";

    private const string ResourceName = "GmailOrganiser.Rules.Prompts.rules-summary-v1.md";

    public static string Template { get; } = Load();

    /// <summary>Prose at a low temperature, short enough for a few sentences.</summary>
    public static ChatOptions CreateOptions() =>
        Llm.LlmCallMeter.NoThink(new() { Temperature = 0.2f, MaxOutputTokens = 600 });

    /// <summary>
    /// The instructions before the first placeholder line are the system message, the rest the user message, so filter
    /// criteria and label names never sit in the system prompt.
    /// </summary>
    public static IList<ChatMessage> Build(IReadOnlyList<FilterDto> filters, IReadOnlyList<SummaryFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(findings);
        var index = Math.Min(Position(FiltersPlaceholder), Position(FindingsPlaceholder));
        var lineStart = Template.LastIndexOf('\n', index) + 1;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["filters"] = RenderFilters(filters),
            ["findings"] = RenderFindings(findings),
        };
        return
        [
            new ChatMessage(ChatRole.System, Template[..lineStart].TrimEnd()),
            new ChatMessage(ChatRole.User, PromptTemplate.Substitute(Template[lineStart..], values)),
        ];
    }

    /// <summary>The answer trimmed, without control characters other than line breaks, at most <see cref="MaxSummaryLength"/> chars; null when empty.</summary>
    public static string? Clean(string? answer)
    {
        if (answer is null)
        {
            return null;
        }

        var sb = new StringBuilder(answer.Length);
        foreach (var c in answer.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (c == '\n' || !char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        var text = sb.ToString().Trim();
        if (text.Length > MaxSummaryLength)
        {
            text = text[..MaxSummaryLength].TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }

    private static int Position(string placeholder)
    {
        var i = Template.IndexOf(placeholder, StringComparison.Ordinal);
        return i >= 0 ? i : throw new InvalidOperationException($"The summary prompt has no {placeholder} placeholder.");
    }

    private static string RenderFilters(IReadOnlyList<FilterDto> filters)
    {
        if (filters.Count == 0)
        {
            return "(none)";
        }

        var lines = filters.Take(MaxFilters).Select(f => $"- id: {OneLine(f.Id)} | {RenderFilter(f)}").ToList();
        if (filters.Count > MaxFilters)
        {
            lines.Add($"[{filters.Count - MaxFilters} more filters omitted]");
        }

        return string.Join('\n', lines);
    }

    private static string RenderFindings(IReadOnlyList<SummaryFinding> findings)
    {
        if (findings.Count == 0)
        {
            return "(none)";
        }

        var lines = findings.Take(MaxFindings).Select((f, i) => string.Create(
            CultureInfo.InvariantCulture,
            $"- finding {i + 1} | kind: {Name(f.Kind)} | status: {Name(f.Status)} | filters: {Ids(f.FilterIds)}"
            + $" | description: {OneLine(f.Description)} | fix: {RenderFix(f)}")).ToList();
        if (findings.Count > MaxFindings)
        {
            lines.Add($"[{findings.Count - MaxFindings} more findings omitted]");
        }

        return string.Join('\n', lines);
    }

    private static string RenderFix(SummaryFinding f)
    {
        var parts = new List<string> { Name(f.FixKind) };
        if (f.Create is { } create)
        {
            parts.Add($"creates a filter ({RenderFilter(create)})");
        }

        if (f.DeleteFilterIds.Count > 0)
        {
            parts.Add($"deletes {Ids(f.DeleteFilterIds)}");
        }

        return string.Join("; ", parts);
    }

    private static string RenderFilter(FilterDto f)
    {
        var labels = f.Action.AddLabels.Count == 0
            ? "-"
            : string.Join(", ", f.Action.AddLabels.Select(l => l.Name is { } name ? OneLine(name) : "(unknown label)"));
        var line = $"criteria: {OneLine(f.CriteriaSummary)} | adds labels: {labels} | skips inbox: {YesNo(f.Action.SkipInbox)}"
            + $" | marks read: {YesNo(f.Action.MarkRead)} | forwards: {YesNo(f.Action.Forwards)}"
            + $" | created by this app: {YesNo(f.CreatedByApp)}";
        return f.DeletedAt is null ? line : line + " | deleted: yes";
    }

    private static string Ids(IReadOnlyList<string> ids) => ids.Count == 0 ? "-" : string.Join(", ", ids.Select(OneLine));

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>Values stay on one line so they can't start a fake filter or finding line.</summary>
    private static string OneLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n', '\v', '\f', '\u0085', '\u2028', '\u2029'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Load()
    {
        using var stream = typeof(RulesSummaryPromptBuilder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded prompt template '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
