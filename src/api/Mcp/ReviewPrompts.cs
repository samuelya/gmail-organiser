using System.ComponentModel;
using System.Globalization;
using System.Text;
using GmailOrganiser.Analysis.Prompts;
using ModelContextProtocol.Server;

namespace GmailOrganiser.Mcp;

/// <summary>
/// The <c>review-pending</c> prompt (DESIGN §6.7): the embedded <c>review-pending.md</c>, served as an MCP prompt and as
/// plain text for the Claude Desktop "copy prompt" button. Tools are named without a server prefix.
/// </summary>
[McpServerPromptType]
public sealed class ReviewPrompts
{
    public const string ReviewPendingName = "review-pending";

    private const string ResourceName = "GmailOrganiser.Mcp.Prompts.review-pending.md";

    private static readonly string Template = Load();

    [McpServerPrompt(Name = ReviewPendingName, Title = "Review pending items")]
    [Description("Reviews the items waiting for Claude in Gmail Organiser and submits one verdict per item.")]
    public static string ReviewPending(
        [Description("Maximum number of items to review, 1 to 100; default 20.")] string? limit = null) =>
        Render(int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : ReviewItemBuilder.DefaultListLimit);

    /// <summary>The prompt text for at most <paramref name="limit"/> items (clamped as <c>list_pending_reviews</c> clamps it).</summary>
    public static string Render(int limit) => PromptTemplate.Substitute(Template, new Dictionary<string, string>
    {
        ["limit"] = Math.Clamp(limit, 1, ReviewItemBuilder.MaxListLimit).ToString(CultureInfo.InvariantCulture),
    });

    private static string Load()
    {
        using var stream = typeof(ReviewPrompts).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded prompt '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
