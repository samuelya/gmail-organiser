using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Fetch;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit;

public sealed class GroupingTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlySet<string> NoAllowlist = new HashSet<string>();
    private static readonly GroupingSettings Defaults = GroupingSettings.From(new AppSettings());
    private static readonly ProtectionSettings AllRules = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MessageRow Msg(int day, string from = "shop@example.com", string subject = "Order 1 shipped") => new()
    {
        Id = $"m{day:D3}",
        FromAddress = from,
        Subject = subject,
        InternalDate = Start.AddDays(day),
        Category = MessageCategory.Promotions,
        LabelIds = ["INBOX"],
    };

    private static MessageGroup GroupOf(IEnumerable<MessageRow> members) =>
        new("k", "shop@example.com", "d", members.OrderByDescending(m => m.InternalDate).ToList(), [], false);

    private static Task<IReadOnlyList<MessageGroup>> GroupAsync(IReadOnlyList<MessageRow> messages, GroupingSettings? settings = null) =>
        new AnalysisGrouper(new NoOpGroupRefiner()).GroupAsync(messages, settings ?? Defaults, NoAllowlist, Ct);

    [Fact]
    public async Task Off_mode_makes_one_individual_group_per_message()
    {
        var groups = await GroupAsync([Msg(1), Msg(2), Msg(3)], Defaults with { Mode = AnalysisGroupingMode.Off });

        groups.Count.ShouldBe(3);
        groups.ShouldAllBe(g => g.Individual && g.Members.Count == 1 && g.RepresentativeIds.Count == 1);
        groups.Select(g => g.Key).ShouldBe(["msg:m003", "msg:m002", "msg:m001"]);
    }

    [Fact]
    public async Task Mail_of_one_sender_filed_under_different_user_labels_never_shares_a_group()
    {
        IReadOnlyList<MessageRow> messages =
        [
            .. Enumerable.Range(1, 3).Select(i => WithLabels(Msg(i, subject: $"Order {i} shipped"), "INBOX", "Label_1")),
            .. Enumerable.Range(4, 3).Select(i => WithLabels(Msg(i, subject: $"Order {i} shipped"), "Label_2", "Label_1")),
            .. Enumerable.Range(7, 3).Select(i => Msg(i, subject: $"Order {i} shipped")),
        ];

        var groups = await GroupAsync(messages, Defaults with { MinGroupSize = 2 });

        groups.Select(g => (g.Key, string.Join(',', g.Members.Select(m => m.Id)))).ShouldBe([
            ("from:shop@example.com|promotions|order # shipped", "m009,m008,m007"),
            ("from:shop@example.com|promotions|order # shipped|labels:Label_1,Label_2", "m006,m005,m004"),
            ("from:shop@example.com|promotions|order # shipped|labels:Label_1", "m003,m002,m001"),
        ]);
    }

    private static MessageRow WithLabels(MessageRow m, params string[] labels)
    {
        m.LabelIds = labels;
        return m;
    }

    [Theory]
    [InlineData(AnalysisGroupingMode.SenderSubject)]
    [InlineData(AnalysisGroupingMode.Auto)]
    public async Task Groups_by_key_and_splits_groups_below_the_minimum(AnalysisGroupingMode mode)
    {
        IReadOnlyList<MessageRow> messages =
        [
            .. Enumerable.Range(1, 5).Select(i => Msg(i, subject: $"Order {i * 111} shipped")),
            Msg(10, from: "other@example.com", subject: "Hello 1"),
            Msg(11, from: "other@example.com", subject: "Hello 2"),
        ];

        var groups = await GroupAsync(messages, Defaults with { Mode = mode });

        groups.Count.ShouldBe(3);
        var shop = groups.Single(g => !g.Individual);
        shop.Key.ShouldBe("from:shop@example.com|promotions|order # shipped");
        shop.Members.Select(m => m.Id).ShouldBe(["m005", "m004", "m003", "m002", "m001"]);
        shop.Display.ShouldBe("Order 555 shipped");
        shop.RepresentativeIds.Count.ShouldBe(3);
        groups.Where(g => g.Individual).Select(g => g.Key).ShouldBe(["msg:m011", "msg:m010"]);
    }

    [Fact]
    public async Task List_groups_show_the_list_suffix()
    {
        var messages = Enumerable.Range(1, 3).Select(i => Msg(i)).ToList();
        messages.ForEach(m => m.ListId = "news.example.com");

        var group = (await GroupAsync(messages)).Single();

        group.Display.ShouldBe("Order 1 shipped (List)");
        group.Key.ShouldStartWith("list:");
    }

    [Fact]
    public async Task Refiner_runs_only_in_auto_mode_after_keying()
    {
        var refiner = new RecordingRefiner();
        var grouper = new AnalysisGrouper(refiner);
        var messages = Enumerable.Range(1, 3).Select(i => Msg(i)).ToList();

        await grouper.GroupAsync(messages, Defaults with { Mode = AnalysisGroupingMode.SenderSubject }, NoAllowlist, Ct);
        refiner.Calls.ShouldBe(0);

        await grouper.GroupAsync(messages, Defaults with { Mode = AnalysisGroupingMode.Auto }, NoAllowlist, Ct);
        refiner.Calls.ShouldBe(1);
        refiner.Seen.ShouldHaveSingleItem().RepresentativeIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refined_groups_are_resorted_newest_first_with_display_and_sender_recomputed()
    {
        var messages = new[] { Msg(1, subject: "Order 1 shipped"), Msg(2, subject: "Hello"), Msg(3, from: "other@example.com", subject: "Late news") };
        var grouper = new AnalysisGrouper(new FuncRefiner(groups =>
            [groups[0] with { Key = "cluster:1", Members = [.. groups.SelectMany(g => g.Members).OrderBy(m => m.InternalDate)] }]));

        var group = (await grouper.GroupAsync(messages, Defaults with { Mode = AnalysisGroupingMode.Auto }, NoAllowlist, Ct)).Single();

        group.Key.ShouldBe("cluster:1");
        group.Members.Select(m => m.Id).ShouldBe(["m003", "m002", "m001"]);
        group.Display.ShouldBe("Late news");
        group.SenderAddress.ShouldBe("other@example.com");
    }

    public static TheoryData<string> BrokenRefinements => new() { "drop", "duplicate", "empty" };

    [Theory]
    [MemberData(nameof(BrokenRefinements))]
    public async Task A_refiner_that_does_not_partition_the_input_fails_loudly(string broken)
    {
        var messages = Enumerable.Range(1, 3).Select(i => Msg(i, subject: $"Subject {(char)('a' + i)}")).ToList();
        var grouper = new AnalysisGrouper(new FuncRefiner(groups => broken switch
        {
            "drop" => [.. groups.Skip(1)],
            "duplicate" => [.. groups, groups[0]],
            _ => [.. groups, groups[0] with { Members = [] }],
        }));

        await Should.ThrowAsync<InvalidOperationException>(
            () => grouper.GroupAsync(messages, Defaults with { Mode = AnalysisGroupingMode.Auto }, NoAllowlist, Ct));
    }

    [Fact]
    public async Task Protected_members_beyond_k_are_analysed_individually()
    {
        var messages = Enumerable.Range(0, 10).Select(i => Msg(i)).ToList();
        messages.ForEach(m => m.HasAttachment = true);
        messages[0].HasAttachment = false;

        var groups = await GroupAsync(messages);

        var group = groups.Single(g => !g.Individual);
        group.Members.Select(m => m.Id).ShouldBe(["m009", "m008", "m007", "m000"]);
        group.RepresentativeIds.ShouldBe(["m007", "m008", "m009"]);
        groups.Where(g => g.Individual).Select(g => g.Key).Order().ShouldBe(Enumerable.Range(1, 6).Select(i => $"msg:m{i:D3}"));
    }

    [Fact]
    public void Representatives_per_group_is_at_least_the_derivation_minimum()
    {
        GroupingSettings.From(new AppSettings { AnalysisRepresentativesPerGroup = 1 }).RepresentativesPerGroup
            .ShouldBe(DerivationRule.MinValidRepresentatives);
    }

    [Fact]
    public void Picker_takes_newest_oldest_longest_then_spread()
    {
        var members = Enumerable.Range(0, 10).Select(i => Msg(i, subject: i == 4 ? "Order 1 shipped with a long note" : "Order 1 shipped")).ToList();

        RepresentativePicker.Pick(GroupOf(members), 3, NoAllowlist, AllRules).ShouldBe(["m009", "m000", "m004"]);
        RepresentativePicker.Pick(GroupOf(members), 5, NoAllowlist, AllRules).ShouldBe(["m009", "m000", "m004", "m002", "m006"]);
    }

    [Fact]
    public void Picker_skips_a_longest_subject_already_picked()
    {
        var members = Enumerable.Range(0, 7).Select(i => Msg(i)).ToList();

        // All subjects equal: no distinct longest subject, so the third pick is the middle of the timeline.
        RepresentativePicker.Pick(GroupOf(members), 3, NoAllowlist, AllRules).ShouldBe(["m006", "m000", "m003"]);
    }

    [Fact]
    public void Every_protected_member_is_a_representative_even_beyond_k()
    {
        var members = Enumerable.Range(0, 10).Select(i => Msg(i)).ToList();
        members[2].HasAttachment = true;
        members[3].LabelIds = ["INBOX", MessageProtection.StarredLabel];
        members[5].LabelIds = [MessageProtection.ImportantLabel];
        members[7].FromAddress = "trusted@example.com";
        var allowlist = new HashSet<string> { "trusted@example.com" };

        var picked = RepresentativePicker.Pick(GroupOf(members), 3, allowlist, AllRules);

        picked.ShouldBe(["m002", "m003", "m005", "m007"]);
    }

    [Fact]
    public void Picker_fills_up_to_k_after_protected_members()
    {
        var members = Enumerable.Range(0, 10).Select(i => Msg(i)).ToList();
        members[5].HasAttachment = true;

        RepresentativePicker.Pick(GroupOf(members), 3, NoAllowlist, AllRules).ShouldBe(["m005", "m009", "m000"]);
    }

    [Fact]
    public void Picker_never_returns_more_than_the_group()
    {
        var members = Enumerable.Range(0, 3).Select(i => Msg(i)).ToList();

        RepresentativePicker.Pick(GroupOf(members), 10, NoAllowlist, AllRules).Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void Protection_rule(bool attachment, bool starred, bool allowlisted, bool expected)
    {
        var m = Msg(1);
        m.HasAttachment = attachment;
        m.LabelIds = starred ? ["INBOX", "STARRED"] : ["INBOX"];

        MessageProtection.IsProtected(m, allowlisted, AllRules).ShouldBe(expected);
    }

    private static RepresentativeOutput Out(string label = "Shopping", bool action = false, bool delete = true, double confidence = 0.9, bool unsubscribe = false) =>
        new(label, action, delete, unsubscribe, confidence);

    [Fact]
    public void Rule_agrees_case_insensitively_with_penalised_minimum_confidence()
    {
        var result = DerivationRule.Decide([Out(confidence: 0.95), Out("shopping ", confidence: 0.8, unsubscribe: true), Out()], 0.10);

        var agreed = result.ShouldBeOfType<Agreed>();
        agreed.TopicLabel.ShouldBe("Shopping");
        agreed.ToBeDeleted.ShouldBeTrue();
        agreed.NeedsAction.ShouldBeFalse();
        agreed.UnsubscribeSuggested.ShouldBeTrue();
        agreed.Confidence.ShouldBe(0.7, 1e-9);
    }

    [Fact]
    public void Rule_clamps_confidence_to_zero()
    {
        DerivationRule.Decide([Out(confidence: 0.05), Out(confidence: 0.5)], 0.10).ShouldBeOfType<Agreed>().Confidence.ShouldBe(0);
    }

    public static TheoryData<RepresentativeOutput?[]> MixedCases => new()
    {
        new RepresentativeOutput?[] { Out(), Out("Newsletters") },
        new RepresentativeOutput?[] { Out(), Out(action: true) },
        new RepresentativeOutput?[] { Out(), Out(delete: false) },
        new RepresentativeOutput?[] { Out(), null, null },
        new RepresentativeOutput?[] { Out(), Out(), null },
        new RepresentativeOutput?[] { Out(), Out(), Out(" ") },
        new RepresentativeOutput?[] { Out() },
        new RepresentativeOutput?[] { },
        new RepresentativeOutput?[] { Out(), Out(confidence: double.NaN) },
        new RepresentativeOutput?[] { Out(), Out(" ") },
    };

    [Theory]
    [MemberData(nameof(MixedCases))]
    public void Rule_is_mixed_on_disagreement_or_any_invalid_output(RepresentativeOutput?[] outputs) =>
        DerivationRule.Decide(outputs, 0.10).ShouldBe(Mixed.Instance);

    private sealed class FuncRefiner(Func<IReadOnlyList<MessageGroup>, IReadOnlyList<MessageGroup>> refine) : IGroupRefiner
    {
        public Task<IReadOnlyList<MessageGroup>> RefineAsync(IReadOnlyList<MessageGroup> groups, GroupingSettings settings, CancellationToken ct) =>
            Task.FromResult(refine(groups));
    }

    private sealed class RecordingRefiner : IGroupRefiner
    {
        public int Calls { get; private set; }
        public IReadOnlyList<MessageGroup> Seen { get; private set; } = [];

        public Task<IReadOnlyList<MessageGroup>> RefineAsync(IReadOnlyList<MessageGroup> groups, GroupingSettings settings, CancellationToken ct)
        {
            Calls++;
            Seen = groups;
            return Task.FromResult(groups);
        }
    }
}
