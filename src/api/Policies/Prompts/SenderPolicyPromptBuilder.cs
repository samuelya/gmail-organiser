using System.Text;
using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Llm;
using GmailOrganiser.Settings;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Policies.Prompts;

/// <summary>
/// The policy prompt for one sender. <see cref="System"/> holds only the instructions and the person's own labels;
/// <see cref="User"/> holds the email-derived profile and the related senders' policies.
/// </summary>
public sealed record SenderPolicyPrompt(string System, string User, string PromptVersion = SenderPolicyPromptBuilder.Version)
{
    /// <summary>The whole prompt as one text, for display and logs of its size.</summary>
    public string Prompt => System + "\n\n" + User;

    /// <summary>What to send: the instructions as the system message, the profile as the user message.</summary>
    public IList<ChatMessage> Messages => [new(ChatRole.System, System), new(ChatRole.User, User)];
}

/// <summary>Turns a <see cref="SenderProfile"/> and the label tree into the sender-policy prompt (DESIGN §6.2). No I/O.</summary>
public static class SenderPolicyPromptBuilder
{
    public const string Version = "sender-policy-v1";

    /// <summary>The template's first line; <c>FakeAnalysisResponder</c> answers a policy prompt by it.</summary>
    public const string Marker = "Task: " + Version;
    public const string ProfileHeading = "Sender profile:";
    public const string HintsHeading = "Approved policies of related senders:";

    private const string ResourceName = "GmailOrganiser.Policies.Prompts.sender-policy-v1.md";
    private static readonly string[] UserPlaceholders = ["{{policyHints}}", "{{profile}}"];

    /// <summary>One line per mail type, so the model reads the same meaning the review page shows.</summary>
    private static readonly Dictionary<MailType, string> MailTypeDefinitions = new()
    {
        [MailType.Personal] = "mail from a person writing to the person",
        [MailType.ActionBill] = "a bill or request the person must act on (pay, sign, reply, book)",
        [MailType.Receipt] = "a receipt, order or payment confirmation",
        [MailType.StatementDocument] = "a statement, contract, policy or other document worth keeping",
        [MailType.AccountAlert] = "a notice about the person's account (changes, limits, renewals)",
        [MailType.Notification] = "an automated update of short-lived interest (shipping, activity, reminders)",
        [MailType.Newsletter] = "editorial content the person subscribed to",
        [MailType.Marketing] = "advertising, offers and promotions",
        [MailType.Social] = "social network activity",
        [MailType.SecurityOtp] = "a one-time code, sign-in or security alert",
    };

    private static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "topicLabel": { "type": ["string", "null"] },
            "isNewLabel": { "type": "boolean" },
            "documentTypeLabel": { "type": ["string", "null"] },
            "mailType": { "type": ["string", "null"] },
            "retentionDays": { "type": ["integer", "null"] },
            "action": { "type": "string" },
            "confidence": { "type": "number" },
            "reason": { "type": "string" },
            "isMixed": { "type": "boolean" },
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "match": {
                    "type": "object",
                    "properties": {
                      "listIdPresent": { "type": ["boolean", "null"] },
                      "listUnsubscribePresent": { "type": ["boolean", "null"] },
                      "fromAddress": { "type": ["string", "null"] },
                      "fromSubdomain": { "type": ["string", "null"] },
                      "category": { "type": ["string", "null"] },
                      "subjectTemplate": { "type": ["string", "null"] },
                      "subjectContains": { "type": ["string", "null"] }
                    }
                  },
                  "topicLabel": { "type": "string" },
                  "documentTypeLabel": { "type": ["string", "null"] },
                  "mailType": { "type": ["string", "null"] },
                  "retentionDays": { "type": ["integer", "null"] },
                  "action": { "type": "string" },
                  "reason": { "type": "string" }
                },
                "required": ["match", "topicLabel", "action", "reason"]
              }
            }
          },
          "required": ["action", "confidence", "reason", "isMixed", "rules"]
        }
        """).RootElement.Clone();

    public static string Template { get; } = Load();

    /// <summary>Schema-constrained JSON at temperature 0 with the analysis <c>num_ctx</c> (#353).</summary>
    public static ChatOptions CreateOptions(int numCtx) => new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(OutputSchema, "senderPolicy"),
        Temperature = 0,
        AdditionalProperties = new() { [LlmCallMeter.NumCtxKey] = numCtx },
    };

    public static SenderPolicyPrompt Build(SenderProfile profile, IReadOnlyList<string> labelTree, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(labelTree);
        ArgumentNullException.ThrowIfNull(settings);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile"] = SenderProfileBuilder.ToPromptText(profile),
            ["labelTree"] = AnalysisPromptBuilder.RenderLabelTree(labelTree),
            ["mailTypes"] = RenderMailTypes(),
            ["actionLabel"] = AnalysisPromptBuilder.OneLine(settings.ActionLabelName),
            ["deleteLabel"] = AnalysisPromptBuilder.OneLine(settings.DeleteLabelName),
            ["documentTypes"] = AnalysisPromptBuilder.RenderDocumentTypes(settings.DocumentTypeParent, labelTree),
            ["policyHints"] = RenderHints(profile.ApprovedPolicyHints),
        };

        var index = UserPlaceholders.Select(p => Template.IndexOf(p, StringComparison.Ordinal)).Where(i => i >= 0).Min();
        var lineStart = Template.LastIndexOf('\n', index) + 1;
        return new SenderPolicyPrompt(
            PromptTemplate.Substitute(Template[..lineStart], values).Trim(),
            PromptTemplate.Substitute(Template[lineStart..], values).Trim());
    }

    private static string RenderMailTypes() => string.Join('\n', Enum.GetValues<MailType>()
        .Select(t => $"- `{SnakeCaseEnumConverter<MailType>.ToDb(t)}`: {MailTypeDefinitions[t]}"));

    private static string RenderHints(IReadOnlyList<PolicyHint> hints)
    {
        if (hints.Count == 0)
        {
            return HintsHeading + " none";
        }

        var sb = new StringBuilder(HintsHeading);
        foreach (var h in hints)
        {
            var label = h.IsMixed ? "mixed" : AnalysisPromptBuilder.OneLine(h.TopicLabel ?? "-");
            sb.Append("\n- ").Append(SnakeCaseEnumConverter<PolicyScope>.ToDb(h.Scope)).Append(' ')
                .Append(AnalysisPromptBuilder.OneLine(h.ScopeKey)).Append(" -> ").Append(label)
                .Append(h.DocumentTypeLabel is { } d ? " + " + AnalysisPromptBuilder.OneLine(d) : "")
                .Append(' ').Append(SnakeCaseEnumConverter<PolicyAction>.ToDb(h.Action));
        }

        return sb.ToString();
    }

    private static string Load()
    {
        using var stream = typeof(SenderPolicyPromptBuilder).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded prompt template '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
