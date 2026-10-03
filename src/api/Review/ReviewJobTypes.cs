namespace GmailOrganiser.Review;

/// <summary>Job types of the Review feature.</summary>
public static class ReviewJobTypes
{
    /// <summary>Applies approved suggestions to Gmail (<see cref="ApplyActionsJob"/>); its dedup key is the batch id.</summary>
    public const string Apply = "apply_actions";

    /// <summary>Reverts a History batch (<see cref="UndoActionsJob"/>); its dedup key is the undo batch's id.</summary>
    public const string Undo = "undo_actions";

    /// <summary>The job types that write Gmail from <c>action_log</c> chunks and keep a pending chunk in their cursor.</summary>
    public static readonly string[] WritesGmail = [Apply, Undo, CleanUp.CleanUpJobTypes.Actions];
}
