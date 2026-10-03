namespace GmailOrganiser.Settings;

/// <summary>
/// Which protection rules are on (block <c>protection</c> of <see cref="AppSettings"/>, #176); all on by default. An
/// allowlisted sender is always protected and has no toggle. Changing a rule re-evaluates nothing already suggested.
/// </summary>
public sealed record ProtectionSettings(bool Attachments = true, bool Starred = true, bool Important = true, bool RepliedThreads = true);
