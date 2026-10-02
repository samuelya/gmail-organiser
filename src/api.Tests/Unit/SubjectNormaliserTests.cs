using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;

namespace GmailOrganiser.Tests.Unit;

public sealed class SubjectNormaliserTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("Weekly Digest", "weekly digest")]
    [InlineData("Re: RE: Fwd: Weekly Digest", "weekly digest")]
    [InlineData("AW: WG: fw: Weekly Digest", "weekly digest")]
    [InlineData("Re[2]: Weekly Digest", "weekly digest")]
    [InlineData("[list] Re: [Example-Team] Weekly   Digest  ", "weekly digest")]
    [InlineData("Weekly digest [external] update", "weekly digest update")]
    [InlineData("Your order 123456 has shipped", "your order # has shipped")]
    [InlineData("Your order #A1 has shipped", "your order #a# has shipped")]
    [InlineData("Ticket #4521 updated", "ticket # updated")]
    [InlineData("Invoice no. 778 attached", "invoice # attached")]
    [InlineData("Invoice Nr 778 attached", "invoice # attached")]
    [InlineData("Statement for 2026-03-31", "statement for #")]
    [InlineData("Login at 2026-03-31T08:15:00Z", "login at #")]
    [InlineData("Statement for 31.03.2026", "statement for #")]
    [InlineData("Statement for 3/31/26", "statement for #")]
    [InlineData("Reminder at 10:30 pm today", "reminder at # today")]
    [InlineData("Delivery on 3 March 2026", "delivery on #")]
    [InlineData("Delivery on Mar 3", "delivery on #")]
    [InlineData("Delivery on 3rd May, 2026", "delivery on #")]
    [InlineData("Webinar on May 12", "webinar on #")]
    [InlineData("Report for March 2026", "report for #")]
    [InlineData("Sale ends 3 Aug", "sale ends #")]
    [InlineData("Order 123 may be delayed", "order # may be delayed")]
    [InlineData("Order 12 may be delayed", "order # may be delayed")]
    [InlineData("Top 10 march madness picks", "top # march madness picks")]
    [InlineData("Only 2 june slots left", "only # june slots left")]
    [InlineData("You saved $12.99", "you saved #")]
    [InlineData("You saved 1.234,56 €", "you saved #")]
    [InlineData("You saved 20 EUR", "you saved #")]
    [InlineData("Sale: 30% off everything", "sale: # off everything")]
    [InlineData("Sale: 12.5 % off", "sale: # off")]
    [InlineData("Version 1.2.3 released", "version # released")]
    [InlineData("The market report", "the market report")]
    [InlineData("Regional update", "regional update")]
    public void Template_normalises_variable_parts(string? subject, string expected) =>
        SubjectNormaliser.Template(subject).ShouldBe(expected);

    [Fact]
    public void Template_is_capped_and_deterministic()
    {
        var subject = string.Concat(Enumerable.Repeat("word ", 400));

        var template = SubjectNormaliser.Template(subject);

        template.Length.ShouldBeLessThanOrEqualTo(SubjectNormaliser.MaxLength);
        template.ShouldNotEndWith(" ");
        SubjectNormaliser.Template(subject).ShouldBe(template);
    }

    [Fact]
    public void Same_template_for_different_numbers()
    {
        SubjectNormaliser.Template("Your order 1001 from 01.02.2026 ($10.00)")
            .ShouldBe(SubjectNormaliser.Template("Re: Your order 98765 from 28.11.2025 ($1,250.00)"));
    }

    [Fact]
    public void GroupKey_uses_the_list_id_when_set()
    {
        var m = new MessageRow { FromAddress = "news@example.com", ListId = " News.Example.COM ", Subject = "Issue 12" };

        GroupKey.For(m).ShouldBe("list:news.example.com|issue #");
        GroupKey.IsList(GroupKey.For(m)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(MessageCategory.Promotions, "from:shop@example.com|promotions|order # shipped")]
    [InlineData(null, "from:shop@example.com|-|order # shipped")]
    public void GroupKey_uses_sender_and_category_otherwise(MessageCategory? category, string expected)
    {
        var m = new MessageRow { FromAddress = "shop@example.com", Category = category, Subject = "Order 42 shipped" };

        GroupKey.For(m).ShouldBe(expected);
        GroupKey.IsList(expected).ShouldBeFalse();
    }
}
