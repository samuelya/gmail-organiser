namespace GmailOrganiser.Settings;

/// <summary>
/// Which protection rules are on (block <c>protection</c> of <see cref="AppSettings"/>, #176); all on by default. An
/// allowlisted sender is always protected and has no toggle. Changing a rule re-evaluates nothing already suggested.
/// </summary>
public sealed record ProtectionSettings(bool Attachments = true, bool Starred = true, bool Important = true, bool RepliedThreads = true)
{
    /// <summary>Normalised domains (#203) whose senders, subdomains included, are allowlisted.</summary>
    public IReadOnlyList<string> AllowlistedDomains { get; init; } = [];

    // Value equality for the list too, as for the toggles.
    public bool Equals(ProtectionSettings? other) =>
        other is not null
        && (Attachments, Starred, Important, RepliedThreads) == (other.Attachments, other.Starred, other.Important, other.RepliedThreads)
        && AllowlistedDomains.SequenceEqual(other.AllowlistedDomains, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Attachments, Starred, Important, RepliedThreads, AllowlistedDomains.Count);
}
