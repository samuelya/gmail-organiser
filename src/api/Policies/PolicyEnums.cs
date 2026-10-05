namespace GmailOrganiser.Policies;

/// <summary>
/// What an approved policy or rule does to a matching message. #359 applies the mapping; a
/// <see cref="Analysis.MailType.ActionBill"/> outcome also gets the action label and stays in the inbox, whatever the action.
/// </summary>
public enum PolicyAction
{
    /// <summary>Label only; the message stays where it is.</summary>
    Keep,

    /// <summary>Label and leave the inbox.</summary>
    Archive,

    /// <summary>The <c>To-Be-Deleted</c> label and archive; the portal never deletes on its own.</summary>
    Delete,

    /// <summary><see cref="Archive"/>, and an unsubscribe is suggested.</summary>
    Unsubscribe,
}

/// <summary>What a policy's <c>ScopeKey</c> names.</summary>
public enum PolicyScope
{
    /// <summary>A canonical sender address.</summary>
    Sender,

    /// <summary>A canonical domain.</summary>
    Domain,

    /// <summary>A normalised List-Id.</summary>
    List,
}

public enum PolicyStatus
{
    Proposed,
    Approved,
    Rejected,
}

/// <summary>Where a sub-rule came from.</summary>
public enum PolicyRuleSource
{
    Llm,
    User,
    Learned,
}
