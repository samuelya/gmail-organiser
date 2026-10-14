using System.Text;
using System.Text.RegularExpressions;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// The analysis prompt template: the built-in embedded <c>analysis-v9.md</c> or the user's override from Settings.
/// Placeholders are <c>{{name}}</c>; substitution is a single pass, so values (email text) never expand further.
/// </summary>
public sealed partial class PromptTemplate
{
    public const string BuiltInVersion = "analysis-v9";
    public const string CustomVersion = "custom";
    public const string EmailsPlaceholder = "{{emails}}";
    public const string MailTypesPlaceholder = "{{mailTypes}}";
    public const string MemoryPlaceholder = "{{memory}}";
    public const string OwnerGuidancePlaceholder = "{{ownerGuidance}}";

    private const string ResourceName = "GmailOrganiser.Analysis.Prompts.analysis-v9.md";

    private PromptTemplate(string version, string text)
    {
        Version = version;
        Text = text;
        split = new Lazy<(string System, string User)>(() => SplitText(text));
    }

    private readonly Lazy<(string System, string User)> split;

    public string Version { get; }

    public string Text { get; }

    /// <summary>The template asks for a mail type (#365); a custom override without the placeholder stores none.</summary>
    public bool AsksMailType => Text.Contains(MailTypesPlaceholder, StringComparison.Ordinal);

    public static PromptTemplate BuiltIn { get; } = new(BuiltInVersion, LoadBuiltIn());

    /// <summary>The override when it is set, otherwise the built-in template.</summary>
    public static PromptTemplate FromSettings(string? templateOverride) =>
        string.IsNullOrWhiteSpace(templateOverride)
            ? BuiltIn
            : new PromptTemplate(CustomVersion, templateOverride.Replace("\r\n", "\n", StringComparison.Ordinal));

    /// <summary>Replaces known placeholders with <paramref name="values"/>; unknown placeholders are left as they are.</summary>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

    /// <summary>
    /// Placeholders whose values come from email content (senders, subjects, bodies, attachment names) or the person's
    /// re-analysis comment (#561).
    /// </summary>
    public static IReadOnlyList<string> EmailDerivedPlaceholders { get; } =
        [EmailsPlaceholder, MemoryPlaceholder, "{{attachments}}", OwnerGuidancePlaceholder];

    /// <summary>
    /// The template split at the first line holding an email-derived placeholder: the instructions before it become
    /// the system message and the rest the user message, so email content never sits in the system prompt.
    /// Without <c>{{emails}}</c> in the user part the emails are appended to it; without <c>{{ownerGuidance}}</c> the
    /// guidance goes at the start of the <c>{{emails}}</c> line (#561), after the memory and before the emails.
    /// Computed once per template.
    /// </summary>
    public (string System, string User) Split() => split.Value;

    private static (string System, string User) SplitText(string text)
    {
        var index = EmailDerivedPlaceholders
            .Select(p => text.IndexOf(p, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        var lineStart = index < 0 ? text.Length : text.LastIndexOf('\n', index) + 1;
        var user = text[lineStart..];
        if (!user.Contains(EmailsPlaceholder, StringComparison.Ordinal))
        {
            user = index < 0 ? EmailsPlaceholder : user + "\n\n" + EmailsPlaceholder;
        }

        if (!user.Contains(OwnerGuidancePlaceholder, StringComparison.Ordinal))
        {
            var emailsLine = user.LastIndexOf('\n', user.IndexOf(EmailsPlaceholder, StringComparison.Ordinal)) + 1;
            user = user.Insert(emailsLine, OwnerGuidancePlaceholder);
        }

        return (text[..lineStart], user);
    }

    private static string LoadBuiltIn()
    {
        using var stream = typeof(PromptTemplate).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded prompt template '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\{\{([a-zA-Z]+)\}\}")]
    private static partial Regex PlaceholderRegex();
}
