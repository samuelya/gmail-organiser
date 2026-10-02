using System.Net;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using Google.Apis.Gmail.v1.Data;

namespace GmailOrganiser.Tests.Unit;

/// <summary>Attachment listing on hand-built payloads, and the fake mailbox serving its synthetic PDFs.</summary>
public sealed class GmailAttachmentsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ReadAttachments_lists_named_parts_through_nested_multiparts_in_order()
    {
        var message = new Message
        {
            Payload = Multipart(
                "multipart/mixed",
                Multipart(
                    "multipart/alternative",
                    new MessagePart { MimeType = "text/plain", Body = new MessagePartBody { Data = "SGk" } },
                    Multipart("multipart/related", Attachment("inline.png", "image/png", "att-1", 10))),
                Attachment("invoice.pdf", "application/pdf", "att-2", 2048),
                new MessagePart
                {
                    MimeType = "message/rfc822",
                    Filename = "forwarded.eml",
                    Body = new MessagePartBody { AttachmentId = "att-3", Size = 500 },
                    Parts = [Attachment("inner.pdf", "application/pdf", "att-4", 99)],
                },
                new MessagePart { MimeType = "application/pdf", Filename = "unsized.pdf", Body = new MessagePartBody { AttachmentId = "att-5" } }),
        };

        GoogleGmailClient.ReadAttachments(message).ShouldBe(
        [
            new GmailAttachment("att-1", "inline.png", "image/png", 10),
            new GmailAttachment("att-2", "invoice.pdf", "application/pdf", 2048),
            new GmailAttachment("att-3", "forwarded.eml", "message/rfc822", 500),
            new GmailAttachment("att-5", "unsized.pdf", "application/pdf", null),
        ]);
    }

    [Fact]
    public void ReadAttachments_does_not_descend_into_an_unnamed_attached_message()
    {
        var message = new Message
        {
            Payload = Multipart(
                "multipart/mixed",
                new MessagePart { MimeType = "text/plain", Body = new MessagePartBody { Data = "SGk" } },
                new MessagePart
                {
                    MimeType = "message/rfc822",
                    Filename = "",
                    Parts = [Multipart("multipart/mixed", Attachment("inner.pdf", "application/pdf", "att-1", 99))],
                }),
        };

        GoogleGmailClient.ReadAttachments(message).ShouldBeEmpty();
    }

    [Fact]
    public void ReadAttachments_decodes_named_parts_sent_inline()
    {
        var message = new Message
        {
            Payload = Multipart(
                "multipart/mixed",
                new MessagePart { MimeType = "text/plain", Filename = "notes.txt", Body = new MessagePartBody { Data = "SGk", Size = 2 } },
                new MessagePart { MimeType = "text/csv", Filename = "bad.csv", Body = new MessagePartBody { Data = "!!" } }),
        };

        var attachments = GoogleGmailClient.ReadAttachments(message);

        attachments.Select(a => (a.AttachmentId, a.Filename, a.Size)).ShouldBe([(null, "notes.txt", 2), (null, "bad.csv", (int?)null)]);
        attachments[0].InlineContent.ShouldBe("Hi"u8.ToArray());
        attachments[1].InlineContent.ShouldBeNull();
    }

    [Fact]
    public void ReadAttachments_of_a_message_without_parts_is_empty()
    {
        GoogleGmailClient.ReadAttachments(new Message()).ShouldBeEmpty();
        GoogleGmailClient.ReadAttachments(new Message { Payload = new MessagePart { MimeType = "text/plain" } }).ShouldBeEmpty();
    }

    [Fact]
    public async Task Fake_serves_the_seeded_pdfs_through_both_methods()
    {
        var client = new FakeGmailClient(new FakeTokenStore(TimeProvider.System), TimeProvider.System);
        var source = client.Messages.First(m => m.Attachments.Count == 2);

        var message = (await client.GetMessageContentAsync(source.Id, Ct)).ShouldNotBeNull();
        var content = await client.GetAttachmentContentAsync(source.Id, message.Attachments[1].AttachmentId!, Ct);

        message.Body.ShouldBe(new GmailMessageBody(source.BodyText, source.BodyHtml));
        message.Attachments.ShouldBe([.. source.Attachments.Select(a => new GmailAttachment(a.AttachmentId, a.Filename, a.MimeType, a.Size))]);
        content.ShouldBe(source.Attachments[1].Content);
        (await client.GetMessageContentAsync("missing", Ct)).ShouldBeNull();
        (await client.GetAttachmentContentAsync(source.Id, "missing", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Fake_attachment_calls_take_injected_failures_and_need_a_connection()
    {
        var store = new FakeTokenStore(TimeProvider.System);
        var client = new FakeGmailClient(store, TimeProvider.System);
        var source = client.Messages.First(m => m.HasAttachment);
        var attachmentId = source.Attachments[0].AttachmentId;

        client.FailNext(HttpStatusCode.InternalServerError, 2);
        await Should.ThrowAsync<Google.GoogleApiException>(() => client.GetMessageContentAsync(source.Id, Ct));
        await Should.ThrowAsync<Google.GoogleApiException>(() => client.GetAttachmentContentAsync(source.Id, attachmentId, Ct));
        (await client.GetAttachmentContentAsync(source.Id, attachmentId, Ct)).ShouldNotBeNull();

        client.FailNext(HttpStatusCode.Unauthorized, 1);
        await Should.ThrowAsync<GmailNotConnectedException>(() => client.GetAttachmentContentAsync(source.Id, attachmentId, Ct));
        await Should.ThrowAsync<GmailNotConnectedException>(() => client.GetMessageContentAsync(source.Id, Ct));
    }

    private static MessagePart Multipart(string mimeType, params MessagePart[] parts) => new() { MimeType = mimeType, Parts = parts };

    private static MessagePart Attachment(string filename, string mimeType, string attachmentId, int size) =>
        new() { MimeType = mimeType, Filename = filename, Body = new MessagePartBody { AttachmentId = attachmentId, Size = size } };
}
