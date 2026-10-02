namespace GmailOrganiser.Analysis;

/// <summary>Job types of the Analysis feature.</summary>
public static class AnalysisJobTypes
{
    /// <summary>One user-started analysis run; its dedup key is the run id, so several runs queue one after another.</summary>
    public const string Run = "analysis_run";
}
