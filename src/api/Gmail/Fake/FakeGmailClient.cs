namespace GmailOrganiser.Gmail.Fake;

/// <summary>A message in the fake mailbox (synthetic data only).</summary>
public sealed record FakeMessage(
    string Id,
    string ThreadId,
    string From,
    string Subject,
    DateTimeOffset Date,
    IReadOnlyList<string> LabelIds);

/// <summary>
/// In-memory Gmail mailbox seeded with synthetic <c>example.com</c> data. Used by <c>GMAIL_FAKE=true</c>
/// (the whole app runs without Google) and by tests. Thread-safe; register as a singleton.
/// </summary>
public sealed class FakeGmailClient : IGmailClient
{
    public const string AccountEmail = "user@example.com";

    private readonly Lock gate = new();
    private readonly List<FakeMessage> messages;
    private readonly long historyId = 1000;

    public FakeGmailClient(TimeProvider time)
        : this(Seed(time.GetUtcNow()))
    {
    }

    public FakeGmailClient(IEnumerable<FakeMessage> messages)
    {
        this.messages = [.. messages];
    }

    public IReadOnlyList<FakeMessage> Messages
    {
        get
        {
            lock (gate)
            {
                return [.. messages];
            }
        }
    }

    public Task<GmailProfile> GetProfileAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(new GmailProfile(AccountEmail, messages.Count, historyId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>A small synthetic mailbox: a few senders at <c>example.com</c>, spread over the last weeks.</summary>
    public static IReadOnlyList<FakeMessage> Seed(DateTimeOffset now)
    {
        (string From, string Subject, string[] Labels)[] templates =
        [
            ("Newsletter <news@example.com>", "This week's digest", ["INBOX", "CATEGORY_PROMOTIONS"]),
            ("Shop <offers@shop.example.com>", "Special offer inside", ["INBOX", "CATEGORY_PROMOTIONS"]),
            ("Alice Example <alice@example.com>", "Lunch next week?", ["INBOX", "UNREAD", "CATEGORY_PERSONAL"]),
            ("Billing <billing@example.com>", "Your invoice is ready", ["INBOX", "CATEGORY_UPDATES"]),
            ("Community <forum@example.com>", "New replies to your topic", ["CATEGORY_FORUMS"]),
        ];

        var seeded = new List<FakeMessage>();
        for (var i = 0; i < 15; i++)
        {
            var t = templates[i % templates.Length];
            seeded.Add(new FakeMessage(
                Id: $"fake-msg-{i + 1:D4}",
                ThreadId: $"fake-thread-{(i % 7) + 1:D4}",
                From: t.From,
                Subject: $"{t.Subject} #{i + 1}",
                Date: now.AddDays(-2 * i),
                LabelIds: t.Labels));
        }

        return seeded;
    }
}
