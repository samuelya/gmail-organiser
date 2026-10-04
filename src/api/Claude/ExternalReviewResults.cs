namespace GmailOrganiser.Claude;

public enum ExternalReviewResult
{
    Ok,
    NotFound,

    /// <summary>The item is not in a status that allows the change.</summary>
    Conflict,

    /// <summary>The target's suggestions are no longer pending.</summary>
    AlreadyDecided,

    /// <summary>Claude asked for a human; there is nothing to accept.</summary>
    NeedsHuman,

    /// <summary>The stored alternative's label is not a valid label path.</summary>
    InvalidVerdict,

    /// <summary>
    /// Pending suggestions are left, but none can take Claude's outcome: it would mark protected mail to-be-deleted, or
    /// (for <c>agree</c>) no member has the outcome Claude reviewed any more.
    /// </summary>
    NotApplicable,
}

public enum CreateExternalReviewsResult
{
    Ok,
    RunNotFound,
    TooManyTargets,
}

public enum ReviewVerdictResult
{
    Ok,
    NotFound,
    AlreadyReviewed,

    /// <summary>Cancelled or unavailable: nobody is waiting for the verdict.</summary>
    Closed,
    Invalid,

    /// <summary>The target's suggestions are no longer pending: the user decided them after sending it to Claude.</summary>
    AlreadyDecided,
}
