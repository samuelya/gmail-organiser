using GmailOrganiser.Analysis;
using GmailOrganiser.Data;
using GmailOrganiser.Gmail;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Mcp;

/// <summary>
/// One label path segment. <paramref name="Id"/> is null for a parent the path implies but Gmail does not have;
/// <paramref name="MessageCount"/> counts the stored messages carrying exactly this label.
/// </summary>
public sealed record LabelNodeDto(
    string Name,
    string Path,
    string? Id,
    int MessageCount,
    bool IsActionLabel,
    bool IsDeleteLabel,
    IReadOnlyList<LabelNodeDto> Children);

/// <param name="DocumentTypeParent">The document-type parent setting; null when document-type labels are off.</param>
/// <param name="DocumentTypes">The existing document-type labels (<see cref="DocumentTypePath.Children"/>).</param>
public sealed record LabelTreeDto(
    string ActionLabel,
    string DeleteLabel,
    string? DocumentTypeParent,
    IReadOnlyList<string> DocumentTypes,
    int LabelCount,
    IReadOnlyList<LabelNodeDto> Labels);

/// <summary>The user's Gmail labels as a tree, with message counts from <c>messages</c>.</summary>
public sealed partial class LabelTreeBuilder(AppDbContext db, LabelCatalog catalog, ISettingsStore settings, ILogger<LabelTreeBuilder> logger)
{
    /// <summary>The user labels' names, sorted, at most <paramref name="max"/>; empty when Gmail is not connected or unavailable.</summary>
    public async Task<IReadOnlyList<string>> NamesAsync(int max, CancellationToken ct) =>
        [.. (await LabelsAsync(ct)).Where(l => l.Type == GmailLabelType.User).Select(l => l.Name).Order(StringComparer.Ordinal).Take(max)];

    /// <exception cref="GmailNotConnectedException">The app is not connected to Gmail.</exception>
    public async Task<LabelTreeDto> BuildAsync(CancellationToken ct)
    {
        var labels = (await catalog.GetAsync(ct)).Where(l => l.Type == GmailLabelType.User).ToList();
        var app = await settings.GetAsync(ct);
        var ids = labels.Select(l => l.Id).ToList();
        var counts = await db.Messages.AsNoTracking()
            .Where(m => !m.DeletedInGmail)
            .SelectMany(m => m.LabelIds)
            .Where(id => ids.Contains(id))
            .GroupBy(id => id)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var byPath = labels
            .GroupBy(l => l.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in byPath.Keys)
        {
            // Every parent of a path is a node, whether or not Gmail has it as a label.
            for (var i = name.IndexOf('/'); i > 0; i = name.IndexOf('/', i + 1))
            {
                paths.Add(name[..i]);
            }

            paths.Add(name);
        }

        // A path's parent is everything before its last '/'; a path without one (or whose parent is empty) is a root.
        static string? Parent(string path) => path.LastIndexOf('/') is > 0 and var i ? path[..i] : null;
        var children = paths.Where(p => Parent(p) is not null).ToLookup(p => Parent(p)!, StringComparer.Ordinal);

        LabelNodeDto Node(string path)
        {
            var label = byPath.GetValueOrDefault(path);
            return new LabelNodeDto(
                path[(path.LastIndexOf('/') + 1)..],
                path,
                label?.Id,
                label is null ? 0 : counts.GetValueOrDefault(label.Id),
                string.Equals(path, app.ActionLabelName, StringComparison.OrdinalIgnoreCase),
                string.Equals(path, app.DeleteLabelName, StringComparison.OrdinalIgnoreCase),
                [.. children[path].Select(Node)]);
        }

        var roots = paths.Where(p => Parent(p) is null).Select(Node).ToList();
        var typeParent = app.DocumentTypeParent;
        var types = DocumentTypePath.Children(typeParent, byPath.Keys);
        return new LabelTreeDto(app.ActionLabelName, app.DeleteLabelName, typeParent, types, labels.Count, roots);
    }

    /// <summary>
    /// All Gmail labels (system and user) from the catalog cache; empty when Gmail is not connected or the label
    /// fetch fails (rate limit, network), so a review item stays readable without label names.
    /// </summary>
    public async Task<IReadOnlyList<GmailLabel>> LabelsAsync(CancellationToken ct)
    {
        try
        {
            return await catalog.GetAsync(ct);
        }
        catch (GmailNotConnectedException)
        {
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLabelsUnavailable(logger, ex.GetType().Name);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gmail labels unavailable for an MCP review item ({ExceptionType}); returning none")]
    private static partial void LogLabelsUnavailable(ILogger logger, string exceptionType);
}
