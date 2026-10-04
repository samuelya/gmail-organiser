namespace GmailOrganiser.Analysis;

/// <summary>
/// The run's Gmail user label names, trimmed and case-insensitive, built once per run: whether a suggested label
/// exists and how Gmail spells it.
/// </summary>
public sealed class LabelTreeIndex
{
    public static readonly LabelTreeIndex Empty = new([]);

    private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

    public LabelTreeIndex(IEnumerable<string> labelTree)
    {
        foreach (var label in labelTree)
        {
            var name = label.Trim();
            if (name.Length > 0)
            {
                names.TryAdd(name, name);
            }
        }
    }

    public bool Contains(string label) => names.ContainsKey(label.Trim());

    /// <summary>The existing label's spelling (trimmed); null when the tree has no such label.</summary>
    public string? Find(string label) => names.GetValueOrDefault(label.Trim());
}
