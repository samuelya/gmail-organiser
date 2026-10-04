namespace GmailOrganiser.Gmail;

/// <summary>Gmail user labels have ids <c>Label_&lt;n&gt;</c>; system labels use upper-case names (<c>INBOX</c>, <c>CATEGORY_*</c>).</summary>
public static class GmailLabelIds
{
    public const string UserPrefix = "Label_";

    public static bool IsUser(string id) => id.StartsWith(UserPrefix, StringComparison.Ordinal);
}
