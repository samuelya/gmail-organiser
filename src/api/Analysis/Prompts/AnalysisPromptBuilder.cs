using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>Turns a batch of emails plus context into the chat messages for one analysis call. No I/O.</summary>
public sealed partial class AnalysisPromptBuilder(PromptTemplate template)
{
    public const int MaxLabelTreeEntries = 500;
    public const string LabelsOmitted = "[more labels omitted]";
    public const string BodyStart = "<email_body>";
    public const string BodyEnd = "</email_body>";
    public const string MemoryHeading = "Similar past decisions by the person:";
    public const int MaxDocumentTypes = DocumentTypePath.MaxChildren;
    public const string DocumentTypesOff = "Document-type labels are switched off: always set `documentTypeLabel` to null.";

    /// <summary>
    /// The answer shape: one object wrapping the per-email array, because Ollama's JSON mode only yields a top-level
    /// object. OllamaSharp sends the schema as Ollama's <c>format</c>, so the model is constrained to it.
    /// </summary>
    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "suggestions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "string" },
                  "topicLabel": { "type": "string" },
                  "isNewLabel": { "type": "boolean" },
                  "needsAction": { "type": "boolean" },
                  "toBeDeleted": { "type": "boolean" },
                  "unsubscribeSuggested": { "type": "boolean" },
                  "confidence": { "type": "number" },
                  "reason": { "type": "string" },
                  "replaceLabels": { "type": "array", "items": { "type": "string" } },
                  "documentTypeLabel": { "type": ["string", "null"] }
                },
                "required": ["id", "topicLabel", "isNewLabel", "needsAction", "toBeDeleted", "unsubscribeSuggested", "confidence", "reason"]
              }
            },
            "filterCriteria": {
              "type": "object",
              "properties": {
                "from": { "type": ["string", "null"] },
                "listId": { "type": ["string", "null"] },
                "subjectContains": { "type": ["string", "null"] }
              }
            }
          },
          "required": ["suggestions"]
        }
        """).RootElement.Clone();

    public string Version => template.Version;

    /// <summary>Schema-constrained JSON at temperature 0: the most reliable structured output across local models.</summary>
    public static ChatOptions CreateOptions() => new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(OutputSchema, "suggestions"),
        Temperature = 0,
    };

    public IList<ChatMessage> Build(PromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["labelTree"] = RenderLabelTree(input.LabelTree),
            ["documentTypes"] = RenderDocumentTypes(input.DocumentTypeParent, input.LabelTree),
            ["memory"] = RenderMemory(input.Memory),
            ["attachments"] = DefuseBodyTags(input.AttachmentsSection ?? string.Empty),
            ["emails"] = RenderEmails(input.Emails),
            ["actionLabel"] = OneLine(input.ActionLabel),
            ["deleteLabel"] = OneLine(input.DeleteLabel),
        };

        var (system, user) = template.Split();
        return
        [
            new ChatMessage(ChatRole.System, PromptTemplate.Substitute(system, values).Trim()),
            new ChatMessage(ChatRole.User, PromptTemplate.Substitute(user, values).Trim()),
        ];
    }

    private static string RenderLabelTree(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
        {
            return "(no labels yet)";
        }

        var lines = labels.Take(MaxLabelTreeEntries).Select(OneLine).ToList();
        if (labels.Count > MaxLabelTreeEntries)
        {
            lines.Add(LabelsOmitted);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The parent, how deep types may go under it (<see cref="DocumentTypePath.LevelsUnder"/>) and the existing types from
    /// the label tree (<see cref="DocumentTypePath.Children(string?, IEnumerable{string}, out bool)"/>, then a note that
    /// more were omitted).
    /// </summary>
    private static string RenderDocumentTypes(string? parent, IReadOnlyList<string> labels)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return DocumentTypesOff;
        }

        var children = DocumentTypePath.Children(parent, labels, out var truncated).Select(l => $"`{OneLine(l)}`").ToList();
        if (truncated)
        {
            children.Add("(more omitted)");
        }

        var existing = children.Count == 0 ? "none yet" : string.Join(", ", children);
        return $"Document-type labels live under `{OneLine(parent.Trim())}`, {DocumentTypePath.LevelsUnder(parent)} deep. Existing: {existing}.";
    }

    private static string RenderMemory(IReadOnlyList<MemoryHint> memory)
    {
        if (memory.Count == 0)
        {
            return MemoryHeading + " none";
        }

        return MemoryHeading + "\n" + string.Join('\n', memory.Select(m => string.Create(CultureInfo.InvariantCulture,
            $"- sender: {OneLine(m.SenderAddress)} | subject: {OneLine(m.SubjectTemplate ?? "-")} | topicLabel: {OneLine(m.TopicLabel)}"
            + $" | type: {(m.DocumentTypeDecided ? OneLine(m.DocumentTypeLabel ?? "-") : "?")}"
            + $" | needsAction: {YesNo(m.NeedsAction)} | toBeDeleted: {YesNo(m.ToBeDeleted)} | outcome: {OneLine(m.Outcome)}"
            + $" | similarity: {m.Similarity:0.00}")));
    }

    private static string RenderEmails(IReadOnlyList<EmailForPrompt> emails)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Emails to classify ({emails.Count}):\n");
        for (var i = 0; i < emails.Count; i++)
        {
            var e = emails[i];
            sb.Append(CultureInfo.InvariantCulture, $"\n### Email {i + 1}\n")
                .Append(CultureInfo.InvariantCulture, $"id: {OneLine(e.Id)}\n")
                .Append(CultureInfo.InvariantCulture, $"from: {OneLine(e.From)}\n")
                .Append(CultureInfo.InvariantCulture, $"name: {OneLine(e.FromName ?? "-")}\n")
                .Append(CultureInfo.InvariantCulture, $"date: {e.Date.ToUniversalTime():yyyy-MM-dd HH:mm} UTC\n")
                .Append(CultureInfo.InvariantCulture, $"category: {OneLine(e.Category ?? "-")}\n")
                .Append(CultureInfo.InvariantCulture, $"List-Unsubscribe present: {YesNo(e.HasListUnsubscribe)}\n")
                .Append(CultureInfo.InvariantCulture, $"has attachment: {YesNo(e.HasAttachment)}\n")
                .Append(CultureInfo.InvariantCulture, $"subject: {OneLine(e.Subject ?? "-")}\n")
                .Append(CultureInfo.InvariantCulture, $"current labels: {CurrentLabels(e.Labels)}\n")
                .Append(BodyStart).Append('\n')
                .Append(DefuseBodyTags(e.Body))
                .Append('\n').Append(BodyEnd).Append('\n');
        }

        return sb.ToString();
    }

    private static string CurrentLabels(IReadOnlyList<string> labels) =>
        labels.Count == 0 ? "-" : string.Join(", ", labels.Take(EmailForPrompt.MaxLabels).Select(OneLine));

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>Header values stay on one line so they can't start a fake field or email block.</summary>
    private static string OneLine(string value) =>
        DefuseBodyTags(string.Join(' ', value.Split(['\r', '\n', '\v', '\f', '\u0085', '\u2028', '\u2029'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    /// <summary>Email text can't open or close a body block: every <c>email_body</c> tag variant loses its <c>&lt;</c>.</summary>
    private static string DefuseBodyTags(string value) => BodyTagRegex().Replace(value, "[$1");

    [GeneratedRegex(@"<(\s*/?\s*email_body)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyTagRegex();
}
