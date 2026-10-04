using System.ComponentModel;
using System.Globalization;
using System.Text;
using GmailOrganiser.Analysis.Prompts;
using ModelContextProtocol.Server;

namespace GmailOrganiser.Mcp;

/// <summary>
/// The MCP prompts (DESIGN §6.7): <c>review-pending</c> (the embedded <c>review-pending.md</c>, also served as plain text
/// for the Claude Desktop "copy prompt" button) and <c>review-label-plan</c>. Tools are named without a server prefix.
/// </summary>
[McpServerPromptType]
public sealed class ReviewPrompts
{
    public const string ReviewPendingName = "review-pending";

    public const string ReviewLabelPlanName = "review-label-plan";

    private static readonly string Template = Load("review-pending.md");

    private static readonly string LabelPlanTemplate = Load("review-label-plan.md");

    [McpServerPrompt(Name = ReviewPendingName, Title = "Review pending items")]
    [Description("Reviews the items waiting for Claude in Gmail Organiser and submits one verdict per item.")]
    public static string ReviewPending(
        [Description("Maximum number of items to review, 1 to 100; default 20.")] string? limit = null) =>
        Render(int.TryParse(limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : ReviewItemBuilder.DefaultListLimit);

    [McpServerPrompt(Name = ReviewLabelPlanName, Title = "Review a label plan")]
    [Description("Reviews a Gmail Organiser label plan against the current labels and submits feedback on it.")]
    public static string ReviewLabelPlan(
        [Description("The plan id; omit for the newest draft plan.")] string? plan_id = null) => RenderLabelPlan(plan_id);

    /// <summary>The label plan prompt for <paramref name="planId"/> (a plan id, else the newest draft).</summary>
    public static string RenderLabelPlan(string? planId) => PromptTemplate.Substitute(LabelPlanTemplate, new Dictionary<string, string>
    {
        ["planArgument"] = Guid.TryParse(planId, out var id) ? $" with `plan_id` {id}" : " without arguments (the newest draft plan)",
    });

    /// <summary>The prompt text for at most <paramref name="limit"/> items (clamped as <c>list_pending_reviews</c> clamps it).</summary>
    public static string Render(int limit) => PromptTemplate.Substitute(Template, new Dictionary<string, string>
    {
        ["limit"] = Math.Clamp(limit, 1, ReviewItemBuilder.MaxListLimit).ToString(CultureInfo.InvariantCulture),
    });

    private static string Load(string file)
    {
        var resourceName = $"GmailOrganiser.Mcp.Prompts.{file}";
        using var stream = typeof(ReviewPrompts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded prompt '{resourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
