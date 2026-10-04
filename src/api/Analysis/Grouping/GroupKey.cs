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
    /// The analysis group: <see cref="For"/> plus the message's sorted user label ids, so mail the person filed
    /// differently never shares a group. Unchanged for mail without user labels.
    /// </summary>
    public static string ForGrouping(MessageRow m)
    {
        var labels = string.Join(',', m.LabelIds.Where(GmailLabelIds.IsUser).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return labels.Length == 0 ? For(m) : $"{For(m)}{LabelsPrefix}{labels}";
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

    public static bool IsList(string key) => key.StartsWith(ListPrefix, StringComparison.Ordinal);
}
