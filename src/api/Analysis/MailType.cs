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
