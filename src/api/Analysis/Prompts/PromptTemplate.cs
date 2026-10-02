using System.Text;
using System.Text.RegularExpressions;

namespace GmailOrganiser.Analysis.Prompts;

/// <summary>
/// The analysis prompt template: the built-in embedded <c>analysis-v1.md</c> or the user's override from Settings.
/// Placeholders are <c>{{name}}</c>; substitution is a single pass, so values (email text) never expand further.
/// </summary>
public sealed partial class PromptTemplate
{
    public const string BuiltInVersion = "analysis-v1";
    public const string CustomVersion = "custom";
    public const string EmailsPlaceholder = "{{emails}}";

    private const string ResourceName = "GmailOrganiser.Analysis.Prompts.analysis-v1.md";

    private PromptTemplate(string version, string text)
    {
        Version = version;
        Text = text;
    }

    public string Version { get; }

    public string Text { get; }

    public static PromptTemplate BuiltIn { get; } = new(BuiltInVersion, LoadBuiltIn());

    /// <summary>The override when it is set, otherwise the built-in template.</summary>
    public static PromptTemplate FromSettings(string? templateOverride) =>
        string.IsNullOrWhiteSpace(templateOverride)
            ? BuiltIn
            : new PromptTemplate(CustomVersion, templateOverride.Replace("\r\n", "\n", StringComparison.Ordinal));

    /// <summary>Replaces known placeholders with <paramref name="values"/>; unknown placeholders are left as they are.</summary>
    public static string Substitute(string text, IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

    /// <summary>Placeholders whose values come from email content (senders, subjects, bodies, attachment names).</summary>
    public static IReadOnlyList<string> EmailDerivedPlaceholders { get; } = [EmailsPlaceholder, "{{memory}}", "{{attachments}}"];

    /// <summary>
    /// Splits the template at the first line holding an email-derived placeholder: the instructions before it become
    /// the system message and the rest the user message, so email content never sits in the system prompt.
    /// Without <c>{{emails}}</c> in the user part the emails are appended to it.
    /// </summary>
    public (string System, string User) Split()
    {
        var index = EmailDerivedPlaceholders
            .Select(p => Text.IndexOf(p, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (index < 0)
        {
            return (Text, EmailsPlaceholder);
        }

        var lineStart = Text.LastIndexOf('\n', index) + 1;
        var user = Text[lineStart..];
        return (Text[..lineStart], user.Contains(EmailsPlaceholder, StringComparison.Ordinal) ? user : user + "\n\n" + EmailsPlaceholder);
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
