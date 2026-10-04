namespace GmailOrganiser.Settings;

/// <summary>Archive inbox threads carrying <see cref="Label"/> once their last message is older than <see cref="Days"/>.</summary>
public sealed record ArchiveRule(string Label, int Days);

/// <summary>
/// The Apps Script auto-archive config (block <c>appsScript</c> of <see cref="AppSettings"/>, #210). The portal only stores it
/// and renders the script's <c>CONFIG</c> block (<see cref="Rules.AppsScriptConfigGenerator"/>); the script does the archiving.
/// </summary>
public sealed record AppsScriptSettings
{
    public IReadOnlyList<ArchiveRule> Rules { get; init; } = [];

    /// <summary>Archive labelled inbox threads that no longer carry the action label.</summary>
    public bool ActionDoneArchive { get; init; } = true;

    /// <summary>Labels whose threads the script never archives.</summary>
    public IReadOnlyList<string> KeepInInboxLabels { get; init; } = [];

    /// <summary>The script only logs what it would archive.</summary>
    public bool DryRun { get; init; } = true;
}
