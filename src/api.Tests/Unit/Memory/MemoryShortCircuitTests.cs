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
    [InlineData("Type", "type/invoice", true, true, "type/invoice")]
    [InlineData(" Type ", "Type/Invoice", true, true, "Type/Invoice")]
    [InlineData("Type", "Other/Invoice", true, false, null)]
    [InlineData("Type", "Type/Invoice/Paid", true, false, null)]
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

    private static Task<IReadOnlyList<ShortCircuitResult?>> ShortCircuit(string? parent, MemoryPattern pattern, params MessageGroup[] groups) =>
        new MemoryShortCircuit(new PatternMemory(new Dictionary<string, MemoryPattern> { [GroupKey.For(Msg(0))] = pattern }))
            .TryAsync(groups, new ShortCircuitContext(Settings, Allowlist.Empty, new LabelTreeIndex(["Topic/Shop"]), Labels, parent), Ct);

    /// <summary>Answers pattern lookups only; the short-circuit calls nothing else.</summary>
    private sealed class PatternMemory(IReadOnlyDictionary<string, MemoryPattern> patterns) : IDecisionMemory
    {
        public Task<IReadOnlyDictionary<string, MemoryPattern>> FindPatternsAsync(
            IReadOnlyCollection<string> scopeKeys, int minApprovals, CancellationToken ct) => Task.FromResult(patterns);

        public Task EmbedAsync(IReadOnlyList<DecisionRow> decisions, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> CanEmbedAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<MessageVectors?> EmbedMessagesAsync(IReadOnlyList<MessageRow> messages, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryHint>> FindSimilarAsync(
            IReadOnlyList<MessageRow> messages, MessageVectors? vectors, int k, CancellationToken ct) => throw new NotSupportedException();
    }
}
