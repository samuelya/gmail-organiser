using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

/// <summary>The configurable protection rules (#176): each toggle on and off, and the allowlist that has none.</summary>
public sealed class MessageProtectionTests
{
    private static readonly ProtectionSettings AllOff = new(Attachments: false, Starred: false, Important: false, RepliedThreads: false);

    public static TheoryData<string, ProtectionSettings, string?> Rules => new()
    {
        { "attachment", new(), "attachment" },
        { "attachment", new(Attachments: false), null },
        { "starred", new(), "starred" },
        { "starred", new(Starred: false), null },
        { "important", new(), "important" },
        { "important", new(Important: false), null },
        { "none", new(), null },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Each_rule_protects_only_while_it_is_on(string flag, ProtectionSettings rules, string? expected)
    {
        var m = Msg(flag);

        MessageProtection.Reason(m, senderAllowlisted: false, rules).ShouldBe(expected);
        MessageProtection.IsProtected(m, senderAllowlisted: false, rules).ShouldBe(expected is not null);
    }

    [Fact]
    public void A_switched_off_rule_falls_through_to_the_next_one_in_order()
    {
        var m = Msg("starred");
        m.HasAttachment = true;
        m.LabelIds = ["INBOX", MessageProtection.StarredLabel, MessageProtection.ImportantLabel];

        MessageProtection.Reason(m, false, new()).ShouldBe("attachment");
        MessageProtection.Reason(m, false, new(Attachments: false)).ShouldBe("starred");
        MessageProtection.Reason(m, false, new(Attachments: false, Starred: false)).ShouldBe("important");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("attachment")]
    public void An_allowlisted_sender_is_protected_whatever_the_rules(string flag)
    {
        var m = Msg(flag);

        MessageProtection.Reason(m, senderAllowlisted: true, AllOff).ShouldBe("allowlisted sender");
        MessageProtection.IsProtected(m, new HashSet<string> { m.FromAddress }, AllOff).ShouldBeTrue();
        MessageProtection.IsProtected(m, new HashSet<string>(), AllOff).ShouldBeFalse();
    }

    [Fact]
    public void A_starred_member_is_not_forced_into_the_representatives_once_the_rule_is_off()
    {
        var members = Enumerable.Range(0, 10).Select(i => Msg("none", i)).ToList();
        members[5].LabelIds = ["INBOX", MessageProtection.StarredLabel];
        var group = new MessageGroup("k", "shop@example.com", "d", members.OrderByDescending(m => m.InternalDate).ToList(), [], false);
        var noAllowlist = new HashSet<string>();

        RepresentativePicker.Pick(group, 3, noAllowlist, new()).ShouldContain("m005");
        RepresentativePicker.Pick(group, 3, noAllowlist, new(Starred: false)).ShouldNotContain("m005");
    }

    private static MessageRow Msg(string flag, int day = 1) => new()
    {
        Id = $"m{day:D3}",
        FromAddress = "shop@example.com",
        Subject = "Order shipped",
        InternalDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day),
        Category = MessageCategory.Promotions,
        HasAttachment = flag == "attachment",
        LabelIds = flag switch
        {
            "starred" => ["INBOX", MessageProtection.StarredLabel],
            "important" => ["INBOX", MessageProtection.ImportantLabel],
            _ => ["INBOX"],
        },
    };
}
