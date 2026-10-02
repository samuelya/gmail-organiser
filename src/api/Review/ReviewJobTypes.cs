namespace GmailOrganiser.Review;

/// <summary>Job types of the Review feature.</summary>
public static class ReviewJobTypes
{
    /// <summary>Applies approved suggestions to Gmail (<see cref="ApplyActionsJob"/>); its dedup key is the batch id.</summary>
    public const string Apply = "apply_actions";
}
