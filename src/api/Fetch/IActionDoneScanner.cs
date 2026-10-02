namespace GmailOrganiser.Fetch;

/// <summary>
/// Runs after the incremental fetch stored a history page: acts on the messages it re-read. Implemented in Review
/// (auto-archive when the action label is removed), so Fetch does not reference Review internals.
/// </summary>
public interface IActionDoneScanner
{
    /// <summary>Must be safe to repeat: a resumed fetch replays the same page.</summary>
    Task ScanAsync(IReadOnlyList<string> refreshedIds, CancellationToken ct);
}
