using GmailOrganiser.Data;

namespace GmailOrganiser.Analysis;

/// <summary>
/// What kind of mail a message or sender sends (DESIGN §6.2). Stored as text (<c>SnakeCaseEnumConverter</c>), so a new
/// type needs no migration.
/// </summary>
public enum MailType
{
    Personal,

    /// <summary>A bill or request to act on; it gets the action label and stays in the inbox whatever the action.</summary>
    ActionBill,
    Receipt,
    StatementDocument,
    AccountAlert,
    Notification,
    Newsletter,
    Marketing,
    Social,
    SecurityOtp,
}

/// <summary>The mail types as the prompts show them (#356, #365), so every model reads the same meaning the review page shows.</summary>
public static class MailTypes
{
    private static readonly Dictionary<MailType, string> Definitions = new()
    {
        [MailType.Personal] = "mail from a person writing to the person",
        [MailType.ActionBill] = "a bill or request the person must act on (pay, sign, reply, book)",
        [MailType.Receipt] = "a receipt, order or payment confirmation",
        [MailType.StatementDocument] = "a statement, contract, policy or other document worth keeping",
        [MailType.AccountAlert] = "a notice about the person's account (changes, limits, renewals)",
        [MailType.Notification] = "an automated update of short-lived interest (shipping, activity, reminders)",
        [MailType.Newsletter] = "editorial content the person subscribed to",
        [MailType.Marketing] = "advertising, offers and promotions",
        [MailType.Social] = "social network activity",
        [MailType.SecurityOtp] = "a one-time code, sign-in or security alert",
    };

    /// <summary>One <c>- `snake_name`: definition</c> line per type, in declaration order.</summary>
    public static string PromptList() => string.Join('\n', Enum.GetValues<MailType>()
        .Select(t => $"- `{SnakeCaseEnumConverter<MailType>.ToDb(t)}`: {Definitions[t]}"));
}
