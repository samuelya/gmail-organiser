using System.Globalization;

namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// The fake mailbox's label catalogue: Gmail's system labels plus a few synthetic, partly nested user labels. New
/// labels get <c>Label_&lt;n&gt;</c> ids as Gmail assigns them. Not thread-safe; <see cref="FakeGmailClient"/> calls it
/// under its lock.
/// </summary>
internal sealed class FakeLabelStore
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

    /// <summary>Creates a user label, or returns the label that already has <paramref name="name"/> (Gmail's 409).</summary>
    public GmailLabel Create(string name)
    {
        if (GoogleGmailClient.FindByName(labels, name) is { } existing)
        {
            return existing;
        }

        var label = new GmailLabel(string.Create(CultureInfo.InvariantCulture, $"Label_{++nextUserId}"), name, GmailLabelType.User);
        labels.Add(label);
        return label;
    }
}
