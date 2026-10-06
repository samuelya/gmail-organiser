using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>The deterministic grouping key: mailing list (or sender and category) plus the subject template.</summary>
public static class GroupKey
{
    public const string ListPrefix = "list:";
    public const string FromPrefix = "from:";
    public const string LabelsPrefix = "|labels:";

    /// <summary>
    /// The analysis group: <see cref="For"/> plus the message's sorted personal label ids, so mail the person filed
    /// differently never shares a group. Unchanged for mail without personal labels.
    /// </summary>
    public static string ForGrouping(MessageRow m, PersonalLabels labels)
    {
        var ids = string.Join(',', labels.IdsOf(m));
        return ids.Length == 0 ? For(m) : $"{For(m)}{LabelsPrefix}{ids}";
    }

    /// <summary>
    /// The label ids a <see cref="ForGrouping"/> key splits by; empty for any other key (also one whose subject template
    /// merely contains the prefix: every id must be a user label id).
    /// </summary>
    public static IReadOnlyList<string> LabelIds(string key)
    {
        var at = key.LastIndexOf(LabelsPrefix, StringComparison.Ordinal);
        var ids = at < 0 ? [] : key[(at + LabelsPrefix.Length)..].Split(',');
        return ids.Length > 0 && ids.All(GmailLabelIds.IsUser) ? ids : [];
    }

    /// <summary>The memory scope (<c>decisions.scope_key</c>): list or sender and category, plus the subject template.</summary>

    public static string For(MessageRow m)
    {
        var template = SubjectNormaliser.Template(m.Subject);
        if (NormaliseListId(m.ListId) is { } listId)
        {
            return $"{ListPrefix}{listId}|{template}";
        }

        var category = m.Category is { } c ? SnakeCaseEnumConverter<MessageCategory>.ToDb(c) : "-";
        return $"{FromPrefix}{m.FromAddress}|{category}|{template}";
    }

    /// <summary>The List-Id as keys compare it: trimmed and lower case; null when blank.</summary>
    public static string? NormaliseListId(string? listId) =>
        string.IsNullOrWhiteSpace(listId) ? null : listId.Trim().ToLowerInvariant();

    /// <summary>A list group's key, deterministic or an embedding cluster of the list (<c>emb:list:</c>, #114).</summary>
    public static bool IsList(string key) =>
        key.StartsWith(ListPrefix, StringComparison.Ordinal)
        || key.StartsWith(EmbeddingGroupRefiner.KeyPrefix + ListPrefix, StringComparison.Ordinal);
}
