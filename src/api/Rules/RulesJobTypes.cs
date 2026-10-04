namespace GmailOrganiser.Rules;

/// <summary>Job types of the Rules feature.</summary>
public static class RulesJobTypes
{
    /// <summary>Applies a label plan's accepted items (<see cref="Labels.LabelPlanApplyJob"/>); its dedup key is the plan id.</summary>
    public const string LabelPlanApply = "label_plan_apply";
}
