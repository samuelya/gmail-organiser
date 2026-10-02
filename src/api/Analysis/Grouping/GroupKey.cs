using GmailOrganiser.Data;
using GmailOrganiser.Fetch;

namespace GmailOrganiser.Analysis.Grouping;

/// <summary>The deterministic grouping key: mailing list (or sender and category) plus the subject template.</summary>
public static class GroupKey
{
    public const string ListPrefix = "list:";
    public const string FromPrefix = "from:";

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
