namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// A deterministic synthetic mailbox: 60 messages from 8 <c>example.com</c> senders over the last months. Newer mail is
/// in the Inbox, older mail is archived; list senders carry <c>List-Id</c>/<c>List-Unsubscribe</c>, and the billing,
/// travel and statement senders attach generated PDFs. Every third message has an html-only body, the next a text-only
/// one, the next both.
/// </summary>
public static class FakeMailboxSeed
{
    public const int MessageCount = 60;
    public const long HistoryId = 1000;

    private sealed record Sender(
        string From,
        string Subject,
        string[] Labels,
        string? ListId = null,
        int AttachmentsPerMessage = 0,
        string AttachmentTitle = "");

    private static readonly Sender[] Senders =
    [
        new("Weekly News <news@example.com>", "This week's digest", ["CATEGORY_PROMOTIONS"], "Weekly News <weekly.news.example.com>"),
        new("\"Shop Offers\" <Offers@Shop.Example.com>", "Special offer inside", ["CATEGORY_PROMOTIONS"], "shop-offers.example.com"),
        new("Alice Example <alice@example.com>", "Lunch next week?", ["UNREAD", "CATEGORY_PERSONAL"]),
        new("Billing <billing@example.com>", "Your invoice is ready", ["CATEGORY_UPDATES"], AttachmentsPerMessage: 1, AttachmentTitle: "Invoice"),
        new("Community Forum <forum@lists.example.com>", "New replies to your topic", ["CATEGORY_FORUMS"], "Forum <forum.lists.example.com>"),
        new("bob@example.com", "Notes from the meeting", []),
        new("Travel Desk <bookings@travel.example.com>", "Your trip itinerary", ["CATEGORY_UPDATES"], AttachmentsPerMessage: 2, AttachmentTitle: "Itinerary"),
        new("\"Example Bank\" <statements@bank.example.com>", "Your monthly statement", [], AttachmentsPerMessage: 1, AttachmentTitle: "Statement"),
    ];

    public static IReadOnlyList<FakeMessage> Create(DateTimeOffset now)
    {
        var messages = new List<FakeMessage>(MessageCount);
        for (var i = 0; i < MessageCount; i++)
        {
            var sender = Senders[i % Senders.Length];
            var number = i + 1;
            var archived = i >= 36 || i % 5 == 4;
            string[] labels = archived ? sender.Labels : ["INBOX", .. sender.Labels];
            var attachments = Enumerable.Range(1, sender.AttachmentsPerMessage)
                .Select(n => CreatePdf(sender.AttachmentTitle, number, n))
                .ToList();

            messages.Add(new FakeMessage(
                Id: $"fake-msg-{number:D4}",
                ThreadId: $"fake-thread-{(i % 23) + 1:D4}",
                From: sender.From,
                Subject: $"{sender.Subject} #{number}",
                Date: now.AddHours(-31 * i),
                LabelIds: labels,
                To: "User <user@example.com>",
                ListId: sender.ListId,
                ListUnsubscribe: sender.ListId is null ? null : $"<https://example.com/unsubscribe/{number:D4}>",
                Snippet: $"Synthetic message {number} for testing.",
                SizeEstimate: 1500 + (i * 397 % 6000),
                HistoryId: (HistoryId - i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Attachments: attachments,
                BodyText: i % 3 == 0 ? null : $"Hello,\n\nSynthetic body {number} from {sender.Subject.ToLowerInvariant()}. Grüße, Ünïcode ✓\n",
                BodyHtml: i % 3 == 1 ? null : $"<html><body><p>Synthetic body {number}: <b>{sender.Subject}</b>. Grüße ✓</p></body></html>"));
        }

        return messages;
    }

    private static FakeAttachment CreatePdf(string title, int number, int index)
    {
        var name = $"{title} {number:D4}-{index}";
        var minimumSize = 1024 + ((number * 7919) + (index * 104729)) % (49 * 1024);
        var content = SyntheticPdf.Create($"{name} - Example", $"Synthetic {title.ToLowerInvariant()} for example.com testing.", minimumSize);
        return new FakeAttachment($"fake-att-{number:D4}-{index}", $"{title.ToLowerInvariant()}-{number:D4}-{index}.pdf", "application/pdf", content.Length, content);
    }
}
