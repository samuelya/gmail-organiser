using GmailOrganiser.Analysis.Attachments;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>A reason that quotes an attachment of its prompt is never stored as is (#74).</summary>
public sealed class AttachmentReasonGuardTests
{
    private static readonly ConvertedAttachment Statement = new(
        "statement.pdf", AttachmentType.Pdf, "| Item | Amount |\n|---|---|\nThe synthetic balance of the example account is due on the first day.", false);

    [Fact]
    public void A_reason_quoting_eight_words_is_replaced_by_one_naming_the_attachment()
    {
        AttachmentReasonGuard.Scrub("Billing: \"the SYNTHETIC balance of the  example account is due\" soon.", [Statement])
            .ShouldBe("Based on the attachment statement.pdf.");
    }

    [Fact]
    public void A_reason_sharing_fewer_words_is_kept()
    {
        const string reason = "Mentions the synthetic balance of the example statement.";

        AttachmentReasonGuard.Scrub(reason, [Statement]).ShouldBe(reason);
        AttachmentReasonGuard.Scrub("Short reason", [Statement]).ShouldBe("Short reason");
    }

    [Fact]
    public void The_neutral_reason_names_the_quoted_attachment_on_one_line()
    {
        var other = new ConvertedAttachment("other\nfile.pdf", AttachmentType.Pdf, "one two three four five six seven eight nine", false);

        AttachmentReasonGuard.Scrub("It says one two three four five six seven eight.", [Statement, other])
            .ShouldBe("Based on the attachment other file.pdf.");
    }
}
