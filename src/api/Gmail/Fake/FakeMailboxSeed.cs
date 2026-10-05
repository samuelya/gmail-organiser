namespace GmailOrganiser.Gmail.Fake;

/// <summary>
/// A deterministic synthetic mailbox: 60 messages from 8 <c>example.com</c> senders over the last months. Newer mail is
/// in the Inbox, older mail is archived; list senders carry <c>List-Id</c>/<c>List-Unsubscribe</c> (the newsletter
/// supports RFC 8058 one-click, the shop only a link, the forum only <c>mailto:</c>) and the bulk headers
/// <c>Precedence</c>/<c>Auto-Submitted</c>, which personal mail never has, and the billing,
/// travel and statement senders attach generated PDFs. Every third message has an html-only body, the next a text-only
/// one, the next both. Alice's lunch message (#3) has a thread of its own; the last message is the user's reply
/// (<c>SENT</c>) in it, so that thread, and only Alice's conversation, is replied. Two archived offers arrive through
/// Hide-My-Email relay addresses (<see cref="RelayAddresses"/>) that decode to the shop's own address.
/// </summary>
public static class FakeMailboxSeed
{
    public const int MessageCount = 63;

    /// <summary>Synthetic relay addresses of the shop sender (<c>offers@shop.example.com</c>), one per relay domain.</summary>
    public static readonly string[] RelayAddresses =
    [
        "offers_at_shop_example_com_ab12cd_ef34gh@icloud.com",
        "offers_at_shop_example_com_k7m2p9_q4r8s1@privaterelay.appleid.com",
    ];

    /// <summary>The thread the user replied to; its received messages are protected from the delete label.</summary>
    public const string RepliedThreadId = "fake-thread-0024";

    /// <summary>The index of Alice's lunch message (#3), the one the user replied to.</summary>
    private const int RepliedIndex = 2;
    public const long HistoryId = 1000;

    private sealed record Sender(
        string From,
        string Subject,
        string[] Labels,
        string? ListId = null,
        int AttachmentsPerMessage = 0,
        string AttachmentTitle = "",
        UnsubscribeKind Unsubscribe = UnsubscribeKind.None,
        string? Precedence = null,
        string? AutoSubmitted = null);

    private enum UnsubscribeKind
    {
        None,
        OneClick,
        Link,
        Mailto,
    }

    private const string OneClickPost = "List-Unsubscribe=One-Click";

    private static readonly Sender[] Senders =
    [
        new("Weekly News <news@example.com>", "This week's digest", ["CATEGORY_PROMOTIONS"], "Weekly News <weekly.news.example.com>", Unsubscribe: UnsubscribeKind.OneClick, Precedence: "bulk", AutoSubmitted: "auto-generated"),
        new("\"Shop Offers\" <Offers@Shop.Example.com>", "Special offer inside", ["CATEGORY_PROMOTIONS"], "shop-offers.example.com", Unsubscribe: UnsubscribeKind.Link, Precedence: "Bulk", AutoSubmitted: "Auto-Generated"),
        new("Alice Example <alice@example.com>", "Lunch next week?", ["UNREAD", "CATEGORY_PERSONAL"]),
        new("Billing <billing@example.com>", "Your invoice is ready", ["CATEGORY_UPDATES"], AttachmentsPerMessage: 1, AttachmentTitle: "Invoice"),
        new("Community Forum <forum@lists.example.com>", "New replies to your topic", ["CATEGORY_FORUMS"], "Forum <forum.lists.example.com>", Unsubscribe: UnsubscribeKind.Mailto, Precedence: "list", AutoSubmitted: "auto-generated"),
        new("bob@example.com", "Notes from the meeting", []),
        new("Travel Desk <bookings@travel.example.com>", "Your trip itinerary", ["CATEGORY_UPDATES"], AttachmentsPerMessage: 2, AttachmentTitle: "Itinerary"),
        new("\"Example Bank\" <statements@bank.example.com>", "Your monthly statement", [], AttachmentsPerMessage: 1, AttachmentTitle: "Statement"),
    ];

    public static IReadOnlyList<FakeMessage> Create(DateTimeOffset now)
    {
        var messages = new List<FakeMessage>(MessageCount);
        var received = MessageCount - 1 - RelayAddresses.Length;
        for (var i = 0; i < received; i++)
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
                // Threads 1..23 group messages round-robin; the replied conversation is kept apart from them.
                ThreadId: i == RepliedIndex ? RepliedThreadId : $"fake-thread-{(i % 23) + 1:D4}",
                From: sender.From,
                Subject: $"{sender.Subject} #{number}",
                Date: now.AddHours(-31 * i),
                LabelIds: labels,
                To: "User <user@example.com>",
                ListId: sender.ListId,
                // RFC 2606 .invalid never resolves: even a real sender could not reach a public host from the seed.
                ListUnsubscribe: sender.Unsubscribe switch
                {
                    UnsubscribeKind.OneClick or UnsubscribeKind.Link => $"<https://unsubscribe.invalid/{number:D4}>",
                    UnsubscribeKind.Mailto => $"<mailto:unsubscribe@example.com?subject=unsubscribe%20{number:D4}>",
                    _ => null,
                },
                Snippet: $"Synthetic message {number} for testing.",
                SizeEstimate: 1500 + (i * 397 % 6000),
                HistoryId: (HistoryId - i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Attachments: attachments,
                BodyText: i % 3 == 0 ? null : $"Hello,\n\nSynthetic body {number} from {sender.Subject.ToLowerInvariant()}. Grüße, Ünïcode ✓\n",
                BodyHtml: i % 3 == 1 ? null : $"<html><body><p>Synthetic body {number}: <b>{sender.Subject}</b>. Grüße ✓</p></body></html>",
                ListUnsubscribePost: sender.Unsubscribe == UnsubscribeKind.OneClick ? OneClickPost : null,
                Precedence: sender.Precedence,
                AutoSubmitted: sender.AutoSubmitted));
        }

        messages.Add(new FakeMessage(
            Id: $"fake-msg-{received + 1:D4}",
            ThreadId: RepliedThreadId,
            From: "User <user@example.com>",
            Subject: "Re: Lunch next week? #3",
            Date: now.AddHours(-31 * 2).AddMinutes(30),
            LabelIds: ["SENT"],
            To: "Alice Example <alice@example.com>",
            Snippet: "Synthetic reply for testing.",
            SizeEstimate: 900,
            HistoryId: (HistoryId - received - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            BodyText: "Sounds good.\n"));
        for (var r = 0; r < RelayAddresses.Length; r++)
        {
            var number = received + 2 + r;
            messages.Add(new FakeMessage(
                Id: $"fake-msg-{number:D4}",
                ThreadId: $"fake-thread-relay-{r + 1:D4}",
                From: $"\"Shop Offers\" <{RelayAddresses[r]}>",
                Subject: $"Special offer inside #{number}",
                Date: now.AddHours(-31 * (received + r)),
                LabelIds: ["CATEGORY_PROMOTIONS"],
                To: "User <user@example.com>",
                Snippet: $"Synthetic message {number} for testing.",
                SizeEstimate: 1200,
                HistoryId: (HistoryId - number).ToString(System.Globalization.CultureInfo.InvariantCulture),
                BodyText: $"Synthetic relayed offer {number}.\n"));
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
