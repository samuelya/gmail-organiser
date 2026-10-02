using GmailOrganiser.Gmail;
using Google.Apis.Gmail.v1.Data;

namespace GmailOrganiser.Tests.Unit;

public sealed class GmailMetadataMapperTests
{
    [Theory]
    [InlineData("Alice Example <Alice@Example.com>", "alice@example.com", "Alice Example")]
    [InlineData("\"Example, Billing\" <billing@example.com>", "billing@example.com", "Example, Billing")]
    [InlineData("\"\" <empty@example.com>", "empty@example.com", null)]
    [InlineData("Bob@Example.com", "bob@example.com", null)]
    [InlineData("  <bare@example.com>  ", "bare@example.com", null)]
    public void ParseFrom_splits_address_and_display_name(string from, string address, string? name)
    {
        var parsed = GmailMetadataMapper.ParseFrom(from);

        parsed.Address.ShouldBe(address);
        parsed.DisplayName.ShouldBe(name);
    }

    [Fact]
    public void ParseFrom_exposes_the_domain()
    {
        GmailMetadataMapper.ParseFrom("Shop <offers@shop.example.com>").Domain.ShouldBe("shop.example.com");
        GmailMetadataMapper.ParseFrom("").Domain.ShouldBe("");
    }

    [Theory]
    [InlineData("Weekly News <weekly.news.example.com>", "weekly.news.example.com")]
    [InlineData("<list.example.com>", "list.example.com")]
    [InlineData("list.example.com", "list.example.com")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void ParseListId_strips_angle_brackets(string? header, string? expected) =>
        GmailMetadataMapper.ParseListId(header).ShouldBe(expected);

    [Fact]
    public void Map_reads_the_metadata_headers_case_insensitively()
    {
        var message = Message("multipart/alternative",
            ("from", "Alice Example <alice@example.com>"),
            ("To", "user@example.com"),
            ("SUBJECT", "Hello"),
            ("List-Id", "News <news.example.com>"),
            ("List-Unsubscribe", "<https://example.com/u/1>"));

        var metadata = GmailMetadataMapper.Map(message);

        metadata.Id.ShouldBe("msg-1");
        metadata.ThreadId.ShouldBe("thread-1");
        metadata.HistoryId.ShouldBe("12345678901234");
        metadata.InternalDate.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        metadata.LabelIds.ShouldBe(["INBOX", "UNREAD"]);
        metadata.From.ShouldBe("Alice Example <alice@example.com>");
        metadata.To.ShouldBe("user@example.com");
        metadata.Subject.ShouldBe("Hello");
        metadata.ListId.ShouldBe("news.example.com");
        metadata.ListUnsubscribe.ShouldBe("<https://example.com/u/1>");
        metadata.Snippet.ShouldBe("Synthetic snippet");
        metadata.SizeEstimate.ShouldBe(4096);
        metadata.HasAttachment.ShouldBeFalse();
    }

    [Fact]
    public void Map_treats_missing_headers_as_null()
    {
        var metadata = GmailMetadataMapper.Map(Message("text/plain"));

        metadata.From.ShouldBe("");
        metadata.To.ShouldBeNull();
        metadata.Subject.ShouldBeNull();
        metadata.ListId.ShouldBeNull();
        metadata.ListUnsubscribe.ShouldBeNull();
    }

    [Theory]
    [InlineData("multipart/mixed", true)]
    [InlineData("Multipart/Mixed", true)]
    [InlineData("multipart/alternative", false)]
    [InlineData("multipart/related", false)]
    [InlineData("text/html", false)]
    public void Map_derives_HasAttachment_from_the_top_level_mime_type(string mimeType, bool expected) =>
        GmailMetadataMapper.Map(Message(mimeType)).HasAttachment.ShouldBe(expected);

    private static Message Message(string mimeType, params (string Name, string Value)[] headers) => new()
    {
        Id = "msg-1",
        ThreadId = "thread-1",
        HistoryId = 12345678901234,
        InternalDate = 1_700_000_000_000,
        LabelIds = ["INBOX", "UNREAD"],
        Snippet = "Synthetic snippet",
        SizeEstimate = 4096,
        Payload = new MessagePart
        {
            MimeType = mimeType,
            Headers = [.. headers.Select(h => new MessagePartHeader { Name = h.Name, Value = h.Value })],
        },
    };
}
