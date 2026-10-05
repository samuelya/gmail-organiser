using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit.Analysis;

public sealed class SingletonPackerTests
{
    private static readonly Guid RunId = new("00000000-0000-0000-0000-000000000376");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ProtectionSettings AllRules = new();

    private static MessageRow Msg(int n, string? from = null, bool attachment = false) => new()
    {
        Id = $"m{n:D3}",
        FromAddress = from ?? $"sender{n}@example.com",
        Subject = $"Subject {n}",
        InternalDate = Start.AddDays(-n),
        LabelIds = ["INBOX"],
        HasAttachment = attachment,
    };

    private static MessageGroup Single(MessageRow m) => new($"msg:{m.Id}", m.FromAddress, m.Subject!, [m], [m.Id], Individual: true);

    private static IReadOnlyList<MessageGroup> Pack(IReadOnlyList<MessageGroup> groups, int size) =>
        SingletonPacker.Pack(groups, RunId, size, Allowlist.Empty, AllRules);

    [Fact]
    public void Packs_singletons_of_different_senders_in_order_of_arrival()
    {
        var groups = Enumerable.Range(1, 20).Select(i => Single(Msg(i))).ToList();

        var packed = Pack(groups, 16);

        packed.Count.ShouldBe(2);
        packed.ShouldAllBe(g => g.Packed && !g.Individual);
        packed[0].Members.Select(m => m.Id).ShouldBe(groups.Take(16).Select(g => g.Members[0].Id));
        packed[1].Members.Count.ShouldBe(4);
        packed[0].RepresentativeIds.ShouldBe(packed[0].Members.Select(m => m.Id));
        (packed[0].Key, packed[1].Key).ShouldBe(($"pack:{RunId}:1", $"pack:{RunId}:2"));
        packed[1].Display.ShouldBe("4 one-off senders");
    }

    [Fact]
    public void A_pack_never_holds_two_messages_of_one_canonical_sender()
    {
        var relayed = Msg(3, from: "relay-1@example.com");
        relayed.CanonicalAddress = "same@example.com";
        var direct = Msg(4, from: "same@example.com");
        var groups = new[] { Msg(1), Msg(2), relayed, direct, Msg(5) }.Select(Single).ToList();

        var packed = Pack(groups, 16);

        packed.Count.ShouldBe(2);
        packed[0].Members.Select(m => m.Id).ShouldBe(["m001", "m002", "m003", "m005"]);
        // The second pack holds one message only, so it stays an individual group at its first member's place.
        (packed[1].Key, packed[1].Individual, packed[1].Packed).ShouldBe(("msg:m004", true, false));
    }

    [Fact]
    public void Keeps_groups_protected_mail_and_lone_singletons_in_place()
    {
        var keyed = new MessageGroup("k", "shop@example.com", "Offer", [Msg(10, "shop@example.com"), Msg(11, "shop@example.com")],
            ["m010", "m011"], Individual: false);
        var protectedSingle = Single(Msg(2, attachment: true));
        var groups = new[] { Single(Msg(1)), keyed, protectedSingle, Single(Msg(3)), Single(Msg(4)) };

        var packed = Pack(groups, 16);

        packed.Count.ShouldBe(3);
        packed[0].Packed.ShouldBeTrue();
        packed[0].Members.Select(m => m.Id).ShouldBe(["m001", "m003", "m004"]);
        packed[1].ShouldBe(keyed);
        packed[2].ShouldBe(protectedSingle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Pack_size_one_is_off(int size)
    {
        var groups = Enumerable.Range(1, 5).Select(i => Single(Msg(i))).ToList();

        Pack(groups, size).ShouldBe(groups);
    }

    [Fact]
    public void Pack_size_is_capped_at_the_maximum()
    {
        var groups = Enumerable.Range(1, 40).Select(i => Single(Msg(i))).ToList();

        var packed = Pack(groups, 100);

        packed.Select(g => g.Members.Count).ShouldBe([SettingsValidation.MaxAnalysisPackSize, 40 - SettingsValidation.MaxAnalysisPackSize]);
    }
}
