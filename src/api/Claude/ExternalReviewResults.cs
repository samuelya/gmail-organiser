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

    /// <summary>The stored alternative's document type is not 1 to <see cref="Analysis.DocumentTypePath.MaxDepth"/> levels under the current document-type parent (or it is off).</summary>
    InvalidDocumentType,

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

    /// <summary>The label plan or a finding does not exist.</summary>
    TargetNotFound,

    /// <summary>The label plan is not a draft, a finding is not open, or one of them has an open item.</summary>
    TargetConflict,
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
