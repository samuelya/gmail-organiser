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
    public void ReadAttachments_lists_named_parts_with_an_attachment_id_through_nested_multiparts_in_order()
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
                new MessagePart { MimeType = "text/plain", Filename = "no-id.txt", Body = new MessagePartBody { Data = "SGk" } },
                new MessagePart
                {
                    MimeType = "message/rfc822",
                    Filename = "forwarded.eml",
                    Body = new MessagePartBody { AttachmentId = "att-3", Size = 500 },
                    Parts = [Attachment("inner.pdf", "application/pdf", "att-4", 99)],
                }),
        };

        GoogleGmailClient.ReadAttachments(message).ShouldBe(
        [
            new GmailAttachment("att-1", "inline.png", "image/png", 10),
            new GmailAttachment("att-2", "invoice.pdf", "application/pdf", 2048),
            new GmailAttachment("att-3", "forwarded.eml", "message/rfc822", 500),
        ]);
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

        var attachments = await client.GetAttachmentsAsync(source.Id, Ct);
        var content = await client.GetAttachmentContentAsync(source.Id, attachments[1].AttachmentId, Ct);

        attachments.ShouldBe([.. source.Attachments.Select(a => new GmailAttachment(a.AttachmentId, a.Filename, a.MimeType, a.Size))]);
        content.ShouldBe(source.Attachments[1].Content);
        (await client.GetAttachmentsAsync("missing", Ct)).ShouldBeEmpty();
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
        await Should.ThrowAsync<Google.GoogleApiException>(() => client.GetAttachmentsAsync(source.Id, Ct));
        await Should.ThrowAsync<Google.GoogleApiException>(() => client.GetAttachmentContentAsync(source.Id, attachmentId, Ct));
        (await client.GetAttachmentContentAsync(source.Id, attachmentId, Ct)).ShouldNotBeNull();

        client.FailNext(HttpStatusCode.Unauthorized, 1);
        await Should.ThrowAsync<GmailNotConnectedException>(() => client.GetAttachmentContentAsync(source.Id, attachmentId, Ct));
        await Should.ThrowAsync<GmailNotConnectedException>(() => client.GetAttachmentsAsync(source.Id, Ct));
    }

    private static MessagePart Multipart(string mimeType, params MessagePart[] parts) => new() { MimeType = mimeType, Parts = parts };

    private static MessagePart Attachment(string filename, string mimeType, string attachmentId, int size) =>
        new() { MimeType = mimeType, Filename = filename, Body = new MessagePartBody { AttachmentId = attachmentId, Size = size } };
}
