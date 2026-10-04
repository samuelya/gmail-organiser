using System.Globalization;

namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// The fake mailbox's label catalogue: Gmail's system labels plus a few synthetic, partly nested user labels. New
/// labels get <c>Label_&lt;n&gt;</c> ids as Gmail assigns them. Not thread-safe; <see cref="FakeGmailClient"/> calls it
/// under its lock.
/// </summary>
public sealed class FakeLabelStore
{
    public static readonly IReadOnlyList<string> SystemLabelIds =
    [
        "INBOX", "UNREAD", "STARRED", "IMPORTANT", "SENT", "DRAFT", "SPAM", "TRASH",
        "CATEGORY_PERSONAL", "CATEGORY_SOCIAL", "CATEGORY_PROMOTIONS", "CATEGORY_UPDATES", "CATEGORY_FORUMS",
    ];

    public static readonly IReadOnlyList<string> SeedUserLabelNames = ["Example", "Example/Nested", "Synthetic Receipts"];

    private readonly List<GmailLabel> labels = [];
    private int nextUserId;

    public FakeLabelStore()
    {
        labels.AddRange(SystemLabelIds.Select(id => new GmailLabel(id, id, GmailLabelType.System)));
        foreach (var name in SeedUserLabelNames)
        {
            Create(name);
        }
    }

    public IReadOnlyList<GmailLabel> All => [.. labels];

    public bool Exists(string id) => labels.Exists(l => string.Equals(l.Id, id, StringComparison.Ordinal));

    /// <summary>Removes the user label <paramref name="id"/>, as the user deleting it in Gmail does.</summary>
    public void Delete(string id) =>
        labels.RemoveAll(l => l.Type == GmailLabelType.User && string.Equals(l.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Renames the user label <paramref name="id"/>, as the user renaming it in Gmail does; the id stays and nested labels
    /// keep their names. Null when there is no such user label.
    /// </summary>
    /// <exception cref="GmailLabelExistsException">Another label already has <paramref name="name"/> (Gmail's 409).</exception>
    public GmailLabel? Rename(string id, string name)
    {
        GmailLimits.EnsureValidLabelName(name);
        var index = labels.FindIndex(l => l.Type == GmailLabelType.User && string.Equals(l.Id, id, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        if (labels.Exists(l => !string.Equals(l.Id, id, StringComparison.Ordinal) && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new GmailLabelExistsException("Another label already has this name.");
        }

        labels[index] = labels[index] with { Name = name };
        return labels[index];
    }

    /// <summary>
    /// Creates a user label, or returns the user label that already has <paramref name="name"/> (Gmail's 409). Reserved
    /// names are refused as Gmail refuses them.
    /// </summary>
    public GmailLabel Create(string name)
    {
        GmailLimits.EnsureValidLabelName(name);
        if (GmailLabel.FindByName(labels.Where(l => l.Type == GmailLabelType.User), name) is { } existing)
        {
            return existing;
        }

        var label = new GmailLabel(string.Create(CultureInfo.InvariantCulture, $"Label_{++nextUserId}"), name, GmailLabelType.User);
        labels.Add(label);
        return label;
    }
}
