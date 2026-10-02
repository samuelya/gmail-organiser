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
        if (!string.IsNullOrWhiteSpace(m.ListId))
        {
            return $"{ListPrefix}{m.ListId.Trim().ToLowerInvariant()}|{template}";
        }

        var category = m.Category is { } c ? SnakeCaseEnumConverter<MessageCategory>.ToDb(c) : "-";
        return $"{FromPrefix}{m.FromAddress}|{category}|{template}";
    }

    public static bool IsList(string key) => key.StartsWith(ListPrefix, StringComparison.Ordinal);
}
