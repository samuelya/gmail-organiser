using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit.Memory;

public sealed class MemoryShortCircuitTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AppSettings Settings = new() { ActionLabelName = "Synthetic Action", DeleteLabelName = "Synthetic Delete" };

    private static readonly PersonalLabels Labels = PersonalLabels.From(
        [
            new GmailLabel("INBOX", "INBOX", GmailLabelType.System),
            new GmailLabel("Label_1", "Topic/Shop", GmailLabelType.User),
            new GmailLabel("Label_2", "Topic/Other", GmailLabelType.User),
            new GmailLabel("Label_3", "Type/Invoice", GmailLabelType.User),
            new GmailLabel("Label_7", "synthetic action", GmailLabelType.User),
            new GmailLabel("Label_8", "Synthetic Delete", GmailLabelType.User),
        ],
        Settings);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MessageRow Msg(int n, params string[] labels) => new()
    {
        Id = $"m{n}",
        FromAddress = "shop@example.com",
        Subject = "Order 1 shipped",
        InternalDate = Start.AddDays(n),
        Category = MessageCategory.Promotions,
        LabelIds = ["INBOX", .. labels],
    };

    private static MessageGroup Group(params MessageRow[] members) =>
        new(GroupKey.ForGrouping(members[0], Labels), "shop@example.com", "d", members, [], Individual: false);

    [Fact]
    public async Task Answers_only_for_groups_without_a_differing_personal_label()
    {
        var memory = new PatternMemory(new Dictionary<string, MemoryPattern>
        {
            [GroupKey.For(Msg(0))] = new("topic/shop", NeedsAction: false, ToBeDeleted: false, Approvals: 5, Agreement: 1, DocumentTypeLabel: null, DocumentTypeDecided: false),
        });
        var context = new ShortCircuitContext(Settings, Allowlist.Empty, new LabelTreeIndex(["Topic/Shop"]), Labels, DocumentTypeParent: null);

        var results = await new MemoryShortCircuit(memory).TryAsync(
            [
                Group(Msg(1), Msg(2)),
                Group(Msg(3, "Label_1"), Msg(4, "Label_1")),
                Group(Msg(5, "Label_2"), Msg(6, "Label_2")),
                Group(Msg(7, "Label_1", "Label_2")),
                Group(Msg(8, "Label_9")),
                Group(Msg(9, "Label_7", "Label_8"), Msg(10, "Label_1", "Label_8")),
            ],
            context,
            Ct);

        // The app's own action and delete labels are not the person's filing: they neither block nor count.
        results.Select(r => r is not null).ShouldBe([true, true, false, false, false, true]);
        results[1]!.Suggestions.Select(s => s.Id).ShouldBe(["m3", "m4"]);
    }

    [Theory]
    [InlineData(null, "Type/Invoice", false, true, null)]
    [InlineData("Type", null, false, false, null)]
    [InlineData("Type", null, true, true, null)]
    [InlineData("Type", "type/invoice", true, true, "Type/invoice")]
    [InlineData("Type", " Type/Invoice ", true, true, "Type/Invoice")]
    [InlineData("Type", "Type/", true, false, null)]
    [InlineData("Type", "Other/Invoice", true, false, null)]
    [InlineData("Type", "type/Utilities/Electricity", true, true, "Type/Utilities/Electricity")]
    [InlineData("Type", "Type/Invoice/Paid/Card", true, true, "Type/Invoice/Paid/Card")]
    [InlineData("Type", "Type/Invoice/Paid/Card/Debit", true, false, null)]
    [InlineData("A/B/C/D", "A/B/C/D/Invoice/Paid", true, false, null)]
    public async Task Document_type_follows_the_parent_and_whether_the_pattern_decided_it(
        string? parent, string? patternType, bool decided, bool answers, string? expectedType)
    {
        var results = await ShortCircuit(parent, new("Topic/Shop", false, false, 5, 1, patternType, decided), Group(Msg(1)));

        (results[0] is not null).ShouldBe(answers);
        if (answers)
        {
            results[0]!.Suggestions.Single().DocumentTypeLabel.ShouldBe(expectedType);
        }
    }

    [Theory]
    [InlineData("Type", true)]
    [InlineData(null, false)]
    public async Task A_member_filed_under_the_memorised_document_type_keeps_its_labels(string? parent, bool answers)
    {
        var results = await ShortCircuit(
            parent, new("Topic/Shop", false, false, 5, 1, "Type/Invoice", DocumentTypeDecided: true),
            Group(Msg(1, "Label_1", "Label_3"), Msg(2, "Label_3")));

        (results[0] is not null).ShouldBe(answers);
    }

    [Theory]
    [InlineData("Travel|Work")]
    [InlineData("")]
    public async Task Suggestions_carry_the_pattern_extras_and_none_without_them(string extras)
    {
        var expected = extras.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var results = await ShortCircuit(
            null, new("Topic/Shop", false, false, 5, 1, null, false, ExtraLabels: expected), Group(Msg(1), Msg(2)));

        results[0].ShouldNotBeNull().Suggestions.ShouldAllBe(s => s.ExtraLabels.SequenceEqual(expected));
    }

    [Theory]
    [InlineData("topic/other", true)]
    [InlineData("", false)]
    public async Task A_member_carrying_one_of_the_pattern_extras_keeps_its_labels(string extras, bool answers)
    {
        var results = await ShortCircuit(
            null, new("Topic/Shop", false, false, 5, 1, null, false, ExtraLabels: extras.Split('|', StringSplitOptions.RemoveEmptyEntries)),
            Group(Msg(1, "Label_1", "Label_2"), Msg(2, "Label_2")));

        (results[0] is not null).ShouldBe(answers);
    }

    [Fact]
    public void Patterns_and_hints_compare_their_extras_by_value()
    {
        MemoryPattern Pattern(params string[] extras) => new("Topic/Shop", false, false, 5, 1, null, false, ExtraLabels: extras);
        MemoryHint Hint(params string[] extras) => new("shop@example.com", null, "Topic/Shop", false, false, "approved", 1, ExtraLabels: extras);

        Pattern("Travel", "Work").ShouldBe(Pattern("Travel", "Work"));
        Pattern("Travel", "Work").GetHashCode().ShouldBe(Pattern("Travel", "Work").GetHashCode());
        Pattern("Travel").ShouldNotBe(Pattern("Work"));
        Pattern().ShouldBe(new MemoryPattern("Topic/Shop", false, false, 5, 1, null, false));
        Hint("Travel", "Work").ShouldBe(Hint("Travel", "Work"));
        Hint("Travel").ShouldNotBe(Hint("Travel", "Work"));
        (Hint("Travel") with { Similarity = 0.5 }).ShouldNotBe(Hint("Travel"));
    }

    private static Task<IReadOnlyList<ShortCircuitResult?>> ShortCircuit(string? parent, MemoryPattern pattern, params MessageGroup[] groups) =>
        new MemoryShortCircuit(new PatternMemory(new Dictionary<string, MemoryPattern> { [GroupKey.For(Msg(0))] = pattern }))
            .TryAsync(groups, new ShortCircuitContext(Settings, Allowlist.Empty, new LabelTreeIndex(["Topic/Shop"]), Labels, parent), Ct);

    /// <summary>Answers pattern lookups only; the short-circuit calls nothing else.</summary>
    private sealed class PatternMemory(IReadOnlyDictionary<string, MemoryPattern> patterns) : IDecisionMemory
    {
        public Task<IReadOnlyDictionary<string, MemoryPattern>> FindPatternsAsync(
            IReadOnlyCollection<string> scopeKeys, int minApprovals, string? documentTypeParent, CancellationToken ct) => Task.FromResult(patterns);

        public Task EmbedAsync(IReadOnlyList<DecisionRow> decisions, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> CanEmbedAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<MessageVectors?> EmbedMessagesAsync(IReadOnlyList<MessageRow> messages, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryHint>> FindSimilarAsync(
            IReadOnlyList<MessageRow> messages, MessageVectors? vectors, int k, string? documentTypeParent,
            IReadOnlyCollection<string> excludeMessageIds, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
