using System.Globalization;
using System.Text;
using System.Text.Json;
using GmailOrganiser.Data;
using GmailOrganiser.Llm;
using GmailOrganiser.Policies.Prompts;
using Microsoft.Extensions.AI;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>Turns a batch of emails plus context into the chat messages for one analysis call. No I/O.</summary>
public sealed class AnalysisPromptBuilder(PromptTemplate template)
{
    public const int MaxLabelTreeEntries = 500;
    public const string LabelsOmitted = "[more labels omitted]";
    public const string BodyStart = "<email_body>";
    public const string BodyEnd = "</email_body>";
    public const string MemoryHeading = "Similar past decisions by the person:";
    public const string GuidanceHeading =
        "Guidance from the person for this re-analysis. Follow it ahead of any past decisions for these emails: it may change "
        + "which labels and flags fit, never the labels you must not use, the rules or the JSON format.";
    public const string GuidanceStart = "<person_guidance>";
    public const string GuidanceEnd = "</person_guidance>";
    public const string PolicyHeading = "Standing policies the person approved for these senders:";
    public const int MaxDocumentTypes = DocumentTypePath.MaxChildren;
    public const string DocumentTypesOff = "Document-type labels are switched off: always set `documentTypeLabel` to null.";
    public const string BlockedLabelsHeading = "Never use these labels, at any level of a label path:";
    public const string TaxonomyLockedText =
        "The label tree is locked: choose `topicLabel` only from the label tree above, spelled exactly as there. Put anything new in "
        + "`proposedNewLabel`; the person approves each new label one by one.";

    /// <summary>
    /// Closes the memory section only when an approved hint above carries extras (#528): the model never hears of
    /// <c>extraLabels</c> otherwise, and the parser keeps only extras from those hints (<see cref="AllowedExtraLabels"/>).
    /// </summary>
    public const string ExtraLabelsInstruction =
        "Additional labels: answer `extraLabels` (array of strings, at most 5) only with labels listed as `extraLabels` in the "
        + "similar past decisions above, only when the email is the same kind as that decision, spelled exactly as there. "
        + "Otherwise answer `extraLabels` as [].";

    /// <summary>
    /// The answer shape: one object wrapping the per-email array, because Ollama's JSON mode only yields a top-level
    /// object. OllamaSharp sends the schema as Ollama's <c>format</c>, so the model is constrained to it.
    /// </summary>
    private static readonly JsonElement OutputSchema = JsonDocument.Parse($$"""
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
                  "proposedNewLabel": { "type": ["string", "null"] },
                  "mailType": {{SenderPolicyPromptBuilder.EnumSchema<MailType>(nullable: false)}},
                  "isNewLabel": { "type": "boolean" },
                  "needsAction": { "type": "boolean" },
                  "toBeDeleted": { "type": "boolean" },
                  "unsubscribeSuggested": { "type": "boolean" },
                  "confidence": { "type": "number" },
                  "reason": { "type": "string" },
                  "replaceLabels": { "type": "array", "items": { "type": "string" } },
                  "documentTypeLabel": { "type": ["string", "null"] },
                  "extraLabels": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["id", "topicLabel", "mailType", "needsAction", "toBeDeleted", "unsubscribeSuggested", "confidence", "reason"]
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

    /// <inheritdoc cref="PromptTemplate.AsksMailType"/>
    public bool AsksMailType => template.AsksMailType;

    /// <summary>
    /// Schema-constrained JSON at temperature 0: the most reliable structured output across local models. Ollama gets
    /// <paramref name="numCtx"/> as <c>num_ctx</c>, else it uses the model's default and truncates a long prompt (#353).
    /// </summary>
    public static ChatOptions CreateOptions(int numCtx) => new()
    {
        ResponseFormat = ChatResponseFormat.ForJsonSchema(OutputSchema, "suggestions"),
        Temperature = 0,
        AdditionalProperties = new() { [LlmCallMeter.NumCtxKey] = numCtx },
    };

    public IList<ChatMessage> Build(PromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["labelTree"] = RenderLabelTree(input.LabelTree),
            ["mailTypes"] = MailTypes.PromptList(),
            ["blockedLabels"] = RenderLabelSet(input.BlockedLabels ?? [], input.TaxonomyLocked),
            ["documentTypes"] = RenderDocumentTypes(input.DocumentTypeParent, input.LabelTree),
            ["memory"] = RenderMemory(input.Memory, input.Policies ?? []),
            ["attachments"] = DefuseBodyTags(input.AttachmentsSection ?? string.Empty),
            ["emails"] = RenderEmails(input.Emails),
            ["actionLabel"] = OneLine(input.ActionLabel),
            ["deleteLabel"] = OneLine(input.DeleteLabel),
            ["ownerGuidance"] = RenderOwnerGuidance(input.OwnerGuidance),
        };

        var (system, user) = template.Split();
        return
        [
            new ChatMessage(ChatRole.System, PromptTemplate.Substitute(system, values).Trim()),
            new ChatMessage(ChatRole.User, PromptTemplate.Substitute(user, values).Trim()),
        ];
    }

    /// <summary>
    /// The person's re-analysis comment (#561) between its tags, with a blank line before and after so the
    /// <c>{{ownerGuidance}}</c> line sits between the memory and the emails; empty without a comment, which leaves the
    /// prompt as it was. The comment arrives trimmed with <c>\n</c> line breaks (<see cref="AnalysisRunService.CheckComment"/>);
    /// tags that could close the section or open a body are defused.
    /// </summary>
    internal static string RenderOwnerGuidance(string? comment) =>
        string.IsNullOrWhiteSpace(comment)
            ? ""
            : $"\n{GuidanceHeading}\n{GuidanceStart}\n{DefuseBodyTags(comment)}\n{GuidanceEnd}\n";

    internal static string RenderLabelTree(IReadOnlyList<string> labels)
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
    internal static string RenderDocumentTypes(string? parent, IReadOnlyList<string> labels)
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

    /// <summary>
    /// The approved label set (#367) after the document types: the blocked names when there are any, then the lock
    /// paragraph when locked; empty otherwise.
    /// </summary>
    internal static string RenderLabelSet(IReadOnlyCollection<string> blocked, bool locked) =>
        (blocked.Count == 0 ? "" : $"\n\n{BlockedLabelsHeading} {string.Join(", ", blocked.Select(b => $"`{OneLine(b)}`"))}.")
        + (locked ? "\n\n" + TaxonomyLockedText : "");

    /// <summary>
    /// The hints, then <see cref="ExtraLabelsInstruction"/> when one shows extras (<see cref="ExtraLabelsOutput.Shown"/>,
    /// each between backticks as allowed), then the policies after a blank line.
    /// </summary>
    private static string RenderMemory(IReadOnlyList<MemoryHint> memory, IReadOnlyList<SenderPolicyHint> policies)
    {
        if (memory.Count == 0)
        {
            return MemoryHeading + " none" + RenderPolicies(policies);
        }

        var extras = memory.Select(m => ExtraLabelsOutput.Shown(m).Select(l => $"`{ExtraLabelsOutput.ShownForm(l)}`").ToList()).ToList();
        return MemoryHeading + "\n" + string.Join('\n', memory.Select((m, i) => string.Create(CultureInfo.InvariantCulture,
            $"- sender: {OneLine(m.SenderAddress)} | subject: {OneLine(m.SubjectTemplate ?? "-")} | topicLabel: {OneLine(m.TopicLabel)}"
            + $" | type: {(m.DocumentTypeDecided ? OneLine(m.DocumentTypeLabel ?? "-") : "?")} | mailType: {MailTypeName(m.MailType)}"
            + $" | needsAction: {YesNo(m.NeedsAction)} | toBeDeleted: {YesNo(m.ToBeDeleted)} | outcome: {OneLine(m.Outcome)}"
            + $" | similarity: {m.Similarity:0.00}")
            + (extras[i].Count == 0 ? "" : $" | extraLabels: {string.Join(", ", extras[i])}")))
            + (extras.Any(e => e.Count > 0) ? "\n\n" + ExtraLabelsInstruction + (policies.Count == 0 ? "" : "\n") : "")
            + RenderPolicies(policies);
    }

    /// <summary>
    /// The extras the model may answer for <paramref name="input"/>'s prompt, from the same hints <see cref="Build"/>
    /// renders: the approved hints' extras (<see cref="ExtraLabelsOutput.Allowed"/>); empty when none has any, or the
    /// template has no <c>{{memory}}</c> to show them.
    /// </summary>
    public IReadOnlyDictionary<string, string> AllowedExtraLabels(PromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ExtraLabelsOutput.Allowed(template.Text.Contains(PromptTemplate.MemoryPlaceholder, StringComparison.Ordinal)
            ? input.Memory.SelectMany(ExtraLabelsOutput.Shown)
            : []);
    }

    /// <summary>
    /// One hint line per approved policy of the emails' senders (#360), after the memory: covered mail never reaches the
    /// model, so these are mixed senders' unmatched mail or a policy approved since the run started.
    /// </summary>
    private static string RenderPolicies(IReadOnlyList<SenderPolicyHint> policies) =>
        policies.Count == 0 ? "" : "\n" + PolicyHeading + "\n" + string.Join('\n', policies.Select(p =>
            $"- {OneLine(p.Scope)}: {OneLine(p.ScopeKey)} | topicLabel: {(p.IsMixed ? "(mixed: decide per email)" : OneLine(p.TopicLabel ?? "-"))}"
            + $" | mailType: {MailTypeName(p.MailType)} | action: {OneLine(p.Action)}"));

    private static string MailTypeName(MailType? type) => type is { } t ? SnakeCaseEnumConverter<MailType>.ToDb(t) : "-";

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
    internal static string OneLine(string value) =>
        DefuseBodyTags(string.Join(' ', value.Split(['\r', '\n', '\v', '\f', '\u0085', '\u2028', '\u2029'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    /// <summary>
    /// Email text and the person's comment can't open or close a body block or the guidance section: every
    /// <c>&lt;</c> that starts a lookalike of an <c>email_body</c> or <c>person_guidance</c> tag becomes <c>[</c>.
    /// </summary>
    private static string DefuseBodyTags(string value)
    {
        StringBuilder? sb = null;
        for (var i = value.IndexOf('<'); i >= 0; i = value.IndexOf('<', i + 1))
        {
            if (IsTagLookalike(value, i + 1))
            {
                sb ??= new StringBuilder(value);
                sb[i] = '[';
            }
        }

        return sb?.ToString() ?? value;
    }

    private const string EmailBodyLetters = "emailbody";
    private const string PersonGuidanceLetters = "personguidance";

    /// <summary>
    /// Reads the span after a <c>&lt;</c> up to the next <c>&lt;</c> or <c>&gt;</c> as its ASCII letters only, lower-cased,
    /// and asks whether they start with a tag name (#573). Everything else is skipped, not matched: the <c>/</c>, separators,
    /// and any invisible character in any plane, so no joiner, filler, selector or tag character can hide a tag.
    /// </summary>
    private static bool IsTagLookalike(string value, int start)
    {
        Span<char> letters = stackalloc char[PersonGuidanceLetters.Length];
        var count = 0;
        for (var i = start; i < value.Length && count < letters.Length; i++)
        {
            var c = value[i];
            if (c is '<' or '>')
            {
                break;
            }

            if (char.IsAsciiLetter(c))
            {
                letters[count++] = char.ToLowerInvariant(c);
            }
        }

        ReadOnlySpan<char> folded = letters[..count];
        return folded.StartsWith(PersonGuidanceLetters, StringComparison.Ordinal)
            || folded.StartsWith(EmailBodyLetters, StringComparison.Ordinal);
    }
}
