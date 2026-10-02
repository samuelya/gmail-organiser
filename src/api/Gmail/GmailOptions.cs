namespace GmailOrganiser.Gmail;

/// <summary>Gmail options, bound from <c>Gmail:*</c>; <c>GMAIL_FAKE</c> (from <c>.env</c>) overrides <see cref="UseFake"/>.</summary>
public sealed class GmailOptions
{
    public const string SectionName = "Gmail";
    public const string FakeEnvironmentKey = "GMAIL_FAKE";

    /// <summary>Google's hard limit per batch request; Gmail recommends at most 50.</summary>
    public const int MaxBatchSize = 100;

    /// <summary>Gmail's per-user quota, in units per second.</summary>
    public const int GmailUnitsPerSecondLimit = 250;

    public const int MaxRetryAttemptsLimit = 20;

    /// <summary>Run the whole app against the in-memory <see cref="Fake.FakeGmailClient"/>, without Google.</summary>
    public bool UseFake { get; set; }

    /// <summary>
    /// Requests per Gmail batch call, 1 to <see cref="MaxBatchSize"/>. A batch goes out as one burst, so its cost
    /// (<c>BatchSize × 5</c> units) must fit in <see cref="QuotaUnitsPerSecond"/>.
    /// </summary>
    public int BatchSize { get; set; } = 40;

    /// <summary>The app's own budget, below Gmail's <see cref="GmailUnitsPerSecondLimit"/>.</summary>
    public int QuotaUnitsPerSecond { get; set; } = 200;

    /// <summary>Attempts (including the first) for a rate-limited request before giving up.</summary>
    public int MaxRetryAttempts { get; set; } = 8;

    /// <summary>Message ids per <c>batchModify</c> call, 1 to <see cref="GmailLimits.BatchModifyMaxIds"/>; tests lower it.</summary>
    public int BatchModifyMaxIds { get; set; } = GmailLimits.BatchModifyMaxIds;
}
