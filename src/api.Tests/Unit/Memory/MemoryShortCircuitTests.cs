using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Fetch;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;

namespace GmailOrganiser.Tests.Unit.Memory;

public sealed class MemoryShortCircuitTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["Label_1"] = "Topic/Shop",
        ["Label_2"] = "Topic/Other",
    };

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
        new(GroupKey.ForGrouping(members[0]), "shop@example.com", "d", members, [], Individual: false);

    [Fact]
    public async Task Answers_only_for_groups_without_a_differing_user_label()
    {
        var memory = new PatternMemory(new Dictionary<string, MemoryPattern>
        {
            [GroupKey.For(Msg(0))] = new("topic/shop", NeedsAction: false, ToBeDeleted: false, Approvals: 5, Agreement: 1),
        });
        var context = new ShortCircuitContext(new AppSettings(), new HashSet<string>(), ["Topic/Shop"], Names);

        var results = await new MemoryShortCircuit(memory).TryAsync(
            [
                Group(Msg(1), Msg(2)),
                Group(Msg(3, "Label_1"), Msg(4, "Label_1")),
                Group(Msg(5, "Label_2"), Msg(6, "Label_2")),
                Group(Msg(7, "Label_1", "Label_2")),
                Group(Msg(8, "Label_9")),
            ],
            context,
            Ct);

        results.Select(r => r is not null).ShouldBe([true, true, false, false, false]);
        results[1]!.Suggestions.Select(s => s.Id).ShouldBe(["m3", "m4"]);
    }

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
