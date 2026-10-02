using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>Turns a batch of emails plus context into the chat messages for one analysis call. No I/O.</summary>
public sealed class AnalysisPromptBuilder(PromptTemplate template)
{
    public const int MaxLabelTreeEntries = 500;
    public const string LabelsOmitted = "[more labels omitted]";
    public const string BodyStart = "<email_body>";
    public const string BodyEnd = "</email_body>";

    public string Version => template.Version;

    /// <summary>JSON mode at temperature 0: the most reliable array output across local models.</summary>
    public static ChatOptions CreateOptions() => new() { ResponseFormat = ChatResponseFormat.Json, Temperature = 0 };

    public IList<ChatMessage> Build(PromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["labelTree"] = RenderLabelTree(input.LabelTree),
            ["memory"] = RenderMemory(input.Memory),
            ["attachments"] = input.AttachmentsSection ?? string.Empty,
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

    private static string RenderMemory(IReadOnlyList<MemoryHint> memory)
    {
        if (memory.Count == 0)
        {
            return "none";
        }

        return string.Join('\n', memory.Select(m => string.Create(CultureInfo.InvariantCulture,
            $"- sender: {OneLine(m.SenderAddress)} | subject: {OneLine(m.SubjectTemplate ?? "-")} | topicLabel: {OneLine(m.TopicLabel)}"
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
                .Append(BodyStart).Append('\n')
                .Append(e.Body.Replace(BodyEnd, "</ email_body>", StringComparison.OrdinalIgnoreCase))
                .Append('\n').Append(BodyEnd).Append('\n');
        }

        return sb.ToString();
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>Header values stay on one line so they can't start a fake field or email block.</summary>
    private static string OneLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
