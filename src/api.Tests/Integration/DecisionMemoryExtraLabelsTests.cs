using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Memory;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Decision memory and the memory short-circuit carrying the additional labels (#527).</summary>
[Collection(PostgresCollection.Name)]
public sealed class DecisionMemoryExtraLabelsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Shop = "shop@example.com";
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Scope = GroupKey.For(Message("s0"));

    private readonly InMemorySettingsStore settings = new(new AppSettings());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Decisions.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Decisions_differing_only_in_extras_are_one_hint_with_the_newest_extras()
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(
            Approval(-1, ["travel", "work"]), Approval(-2, []), Approval(0, ["Travel", "Work"]), Approval(-3, ["Work"]),
            Approval(-4, ["Home"], "Other"));
        await db.SaveChangesAsync(Ct);

        var hints = await Memory(db).FindSimilarAsync([Message("m9")], vectors: null, k: 5, documentTypeParent: null, [], Ct);

        // The prompt renders a hint without its extras (#528 adds them): extras must not fill k slots with lines that look alike.
        hints.Select(h => (h.TopicLabel, string.Join(',', h.ExtraLabels))).ShouldBe([("Shopping", "Travel,Work"), ("Other", "Home")]);
    }

    [Theory]
    [InlineData("Travel|Work", "travel|Home", "Travel")]
    [InlineData("Travel|Work", "work|TRAVEL", "Travel|Work")]
    [InlineData("Travel|Work", "", "")]
    [InlineData("", "Travel", "")]
    [InlineData("Work", "Travel", "")]
    public async Task Patterns_carry_the_extras_every_approval_has_and_extras_never_break_consistency(
        string latest, string older, string expected)
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(Approval(0, Split(latest)), Approval(-1, Split(older)), Approval(-2, Split(latest)));
        await db.SaveChangesAsync(Ct);

        var patterns = await Memory(db).FindPatternsAsync([Scope], minApprovals: 3, documentTypeParent: null, Ct);

        patterns[Scope].ExtraLabels.ShouldBe(Split(expected));
    }

    [Fact]
    public async Task Pattern_extras_come_from_the_newest_approvals_so_older_ones_without_extras_never_block_them()
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(
            [Approval(0, ["Work"]), Approval(-1, ["work", "Travel"]), Approval(-2, ["Work"]), .. Enumerable.Range(3, 40).Select(d => Approval(-d, []))]);
        await db.SaveChangesAsync(Ct);

        var memory = Memory(db);
        var window = await memory.FindPatternsAsync([Scope], minApprovals: 3, documentTypeParent: null, Ct);
        var wider = await memory.FindPatternsAsync([Scope], minApprovals: 4, documentTypeParent: null, Ct);

        (window[Scope].Approvals, string.Join(',', window[Scope].ExtraLabels)).ShouldBe((43, "Work"));
        wider[Scope].ExtraLabels.ShouldBeEmpty();
    }

    [Fact]
    public async Task Short_circuit_suggestions_carry_the_pattern_extras()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(Approval(0, ["Travel", "Work"]), Approval(-1, ["Travel"]), Approval(-2, ["travel", "Home"]));
            await db.SaveChangesAsync(Ct);
        }

        (await CoverAsync()).ShouldNotBeNull().Suggestions.Single().ExtraLabels.ShouldBe(["Travel"]);
    }

    [Fact]
    public async Task Without_extras_hints_patterns_and_short_circuit_suggestions_are_as_before()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(Approval(0, []), Approval(-1, []), Approval(-2, []));
            await db.SaveChangesAsync(Ct);
        }

        await using var check = postgres.CreateDbContext();
        var hints = await Memory(check).FindSimilarAsync([Message("m9")], vectors: null, k: 5, documentTypeParent: null, [], Ct);
        var pattern = (await Memory(check).FindPatternsAsync([Scope], minApprovals: 3, documentTypeParent: null, Ct))[Scope];
        var suggestion = (await CoverAsync()).ShouldNotBeNull().Suggestions.Single();

        hints.Single().ShouldBe(new MemoryHint(Shop, hints[0].SubjectTemplate, "Shopping", false, false, "approved", 1.0));
        pattern.ShouldBe(new MemoryPattern("Shopping", false, false, 3, 1.0, null, false));
        (suggestion.TopicLabel, suggestion.ExtraLabels.Count).ShouldBe(("Shopping", 0));
    }

    [Fact]
    public async Task A_compare_run_never_sees_its_own_messages_decisions_extras_included()
    {
        var own = Approval(0, ["Travel"]);
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(own, Approval(-1, []));
        await db.SaveChangesAsync(Ct);

        var hints = await Memory(db).FindSimilarAsync([Message("m9")], vectors: null, k: 5, documentTypeParent: null, [own.MessageId!], Ct);

        hints.ShouldHaveSingleItem().ExtraLabels.ShouldBeEmpty();
    }

    private static string[] Split(string extras) => extras.Split('|', StringSplitOptions.RemoveEmptyEntries);

    private async Task<ShortCircuitResult?> CoverAsync()
    {
        await using var db = postgres.CreateDbContext();
        var current = await settings.GetAsync(Ct);
        var context = new ShortCircuitContext(
            current, Allowlist.Empty, new LabelTreeIndex(["Shopping"]), PersonalLabels.From([], current), DocumentTypeParent: null);
        var group = new MessageGroup(Scope, Shop, "d", [Message("m2")], [], Individual: false);
        return (await new MemoryShortCircuit(Memory(db)).TryAsync([group], context, Ct))[0];
    }

    private DecisionMemory Memory(AppDbContext db) => MemoryTestFactory.Memory(db, settings, new FakeEmbeddingGenerator());

    private static MessageRow Message(string id) => new()
    {
        Id = id,
        ThreadId = $"t-{id}",
        FromAddress = Shop,
        Subject = "Order 12345 shipped",
        InternalDate = Now,
        FetchedAt = Now,
        UpdatedAt = Now,
    };

    private static DecisionRow Approval(int days, string[] extras, string topic = "Shopping") => new()
    {
        Id = Guid.NewGuid(),
        MessageId = $"old-{Guid.NewGuid():N}",
        SenderAddress = Shop,
        SubjectTemplate = SubjectNormaliser.Template(Message("s0").Subject),
        ScopeKey = Scope,
        TopicLabel = topic,
        ExtraLabels = extras,
        Outcome = DecisionOutcome.Approved,
        Source = SuggestionSource.Llm,
        CreatedAt = Now.AddDays(days),
    };
}
