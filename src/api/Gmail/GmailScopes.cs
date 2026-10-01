namespace GmailOrganiser.Gmail;

/// <summary>
/// The only OAuth scopes the app requests. Never add the full-access <c>mail.google.com</c> scope.
/// </summary>
public static class GmailScopes
{
    public const string Modify = "https://www.googleapis.com/auth/gmail.modify";
    public const string SettingsBasic = "https://www.googleapis.com/auth/gmail.settings.basic";
    public const string Labels = "https://www.googleapis.com/auth/gmail.labels";

    public static IReadOnlyList<string> All { get; } = [Modify, SettingsBasic, Labels];
}
