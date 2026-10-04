using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Rules;

/// <summary>
/// Renders the <c>const CONFIG = {...};</c> block of <c>scripts/apps-script/auto-archive.gs</c> from the saved settings. The key
/// names and <see cref="ScriptVersion"/> are frozen with the script (#209); the layout is fixed so the output is comparable as text.
/// </summary>
public static class AppsScriptConfigGenerator
{
    public const int ScriptVersion = 1;
    public const int PageSize = 100;
    public const int MaxRuntimeSeconds = 280;

    // JSON string literals are valid JS literals; the relaxed encoder keeps non-ASCII label names readable.
    private static readonly JsonSerializerOptions Literal = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Generate(AppSettings settings)
    {
        var script = settings.AppsScript;
        var text = new StringBuilder();
        text.Append("const CONFIG = {\n");
        text.Append(CultureInfo.InvariantCulture, $"  scriptVersion: {ScriptVersion},\n");
        AppendList(text, "labelRules", script.Rules.Select(r =>
            string.Create(CultureInfo.InvariantCulture, $"{{ label: {Quote(r.Label)}, days: {r.Days} }}")));
        text.Append(CultureInfo.InvariantCulture, $"  actionLabel: {Quote(settings.ActionLabelName)},\n");
        text.Append(CultureInfo.InvariantCulture, $"  actionDoneArchive: {Bool(script.ActionDoneArchive)},\n");
        AppendList(text, "keepInInboxLabels", script.KeepInInboxLabels.Select(Quote));
        text.Append(CultureInfo.InvariantCulture, $"  pageSize: {PageSize},\n");
        text.Append(CultureInfo.InvariantCulture, $"  maxRuntimeSeconds: {MaxRuntimeSeconds},\n");
        text.Append(CultureInfo.InvariantCulture, $"  dryRun: {Bool(script.DryRun)},\n");
        text.Append("};\n");
        return text.ToString();
    }

    private static void AppendList(StringBuilder text, string key, IEnumerable<string> items)
    {
        var entries = items.ToList();
        if (entries.Count == 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {key}: [],\n");
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"  {key}: [\n");
        foreach (var entry in entries)
        {
            text.Append(CultureInfo.InvariantCulture, $"    {entry},\n");
        }

        text.Append("  ],\n");
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, Literal);

    private static string Bool(bool value) => value ? "true" : "false";
}
