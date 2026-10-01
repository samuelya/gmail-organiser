namespace GmailOrganiser.Gmail;

/// <summary>Gmail options, bound from <c>Gmail:*</c>; <c>GMAIL_FAKE</c> (from <c>.env</c>) overrides <see cref="UseFake"/>.</summary>
public sealed class GmailOptions
{
    public const string SectionName = "Gmail";
    public const string FakeEnvironmentKey = "GMAIL_FAKE";

    /// <summary>Run the whole app against the in-memory <see cref="Fake.FakeGmailClient"/>, without Google.</summary>
    public bool UseFake { get; set; }
}
