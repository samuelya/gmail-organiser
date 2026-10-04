using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Grouping;
using GmailOrganiser.Data;
using GmailOrganiser.Fetch;
using GmailOrganiser.Gmail;
using GmailOrganiser.Llm.Fake;
using GmailOrganiser.Memory;
using GmailOrganiser.Review;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GmailOrganiser.Tests.Integration;

/// <summary>Decision memory and the memory short-circuit learning the document-type label (#241).</summary>
[Collection(PostgresCollection.Name)]
public sealed class DecisionMemoryDocumentTypeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Shop = "shop@example.com";
    private const string Parent = "Type";
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Scope = GroupKey.For(Message("s0"));

    private readonly InMemorySettingsStore settings = new(new AppSettings());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var db = postgres.CreateDbContext();
        await db.Decisions.ExecuteDeleteAsync(Ct);
        await db.Suggestions.ExecuteDeleteAsync(Ct);
        await db.AnalysisRuns.ExecuteDeleteAsync(Ct);
        await db.Messages.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(Parent, true)]
    [InlineData(null, false)]
    public async Task Recorder_writes_the_type_label_and_whether_a_parent_was_set(string? parent, bool decided)
    {
        settings.Current = settings.Current with { DocumentTypeParent = parent };
        await RecordAsync(Message("m1"), "Type/Invoice");

        await using var db = postgres.CreateDbContext();
        var row = await db.Decisions.AsNoTracking().SingleAsync(Ct);
        (row.DocumentTypeLabel, row.DocumentTypeDecided).ShouldBe(("Type/Invoice", decided));
    }

    [Theory]
    [InlineData("Type/Invoice", "type/invoice", true)]
    [InlineData(null, null, true)]
    [InlineData("Type/Invoice", null, false)]
    [InlineData("Type/Invoice", "Type/Receipt", false)]
    public async Task Decided_approvals_must_agree_on_the_type_label(string? latest, string? older, bool agrees)
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(
            Approval(0, latest, decided: true), Approval(-1, older, decided: true), Approval(-2, older, decided: true));
        await db.SaveChangesAsync(Ct);

        var patterns = await Memory(db).FindPatternsAsync([Scope], minApprovals: 3, Ct);

        patterns.ContainsKey(Scope).ShouldBe(agrees);
        if (agrees)
        {
            (patterns[Scope].DocumentTypeLabel, patterns[Scope].DocumentTypeDecided).ShouldBe((latest, true));
        }
    }

    [Fact]
    public async Task Old_approvals_go_to_the_model_once_and_the_next_approval_relearns_the_type()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(Enumerable.Range(1, 20).Select(i => Approval(-i, type: null, decided: false)));
            await db.SaveChangesAsync(Ct);
        }

        // Parent off: the old behaviour, no type.
        (await CoverAsync(parent: null)).ShouldNotBeNull().Suggestions.Single().DocumentTypeLabel.ShouldBeNull();

        // Parent on: the latest approval predates it, so the group goes to the model.
        settings.Current = settings.Current with { DocumentTypeParent = Parent };
        (await CoverAsync(Parent)).ShouldBeNull();

        // One approval with the parent set; the 20 old ones do not contradict it.
        await RecordAsync(Message("m1"), "Type/Invoice");
        var covered = (await CoverAsync(Parent)).ShouldNotBeNull().Suggestions.Single();
        covered.DocumentTypeLabel.ShouldBe("Type/Invoice");

        // Turning the parent off again restores the old answer.
        (await CoverAsync(parent: null)).ShouldNotBeNull().Suggestions.Single().DocumentTypeLabel.ShouldBeNull();
    }

    [Fact]
    public async Task Similar_decision_hints_carry_the_type_label()
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(Approval(0, "Type/Invoice", decided: true), Approval(-1, type: null, decided: true));
        await db.SaveChangesAsync(Ct);

        var hints = await Memory(db).FindSimilarAsync([Message("m9")], vectors: null, k: 5, Ct);

        hints.Select(h => h.DocumentTypeLabel).ShouldBe(["Type/Invoice", null]);
    }

    private async Task<ShortCircuitResult?> CoverAsync(string? parent)
    {
        await using var db = postgres.CreateDbContext();
        var current = await settings.GetAsync(Ct);
        var context = new ShortCircuitContext(
            current, Allowlist.Empty, new LabelTreeIndex(["Shopping"]), PersonalLabels.From([], current), parent);
        var message = Message("m2");
        var group = new MessageGroup(Scope, Shop, "d", [message], [], Individual: false);
        return (await new MemoryShortCircuit(Memory(db)).TryAsync([group], context, Ct))[0];
    }

    private async Task RecordAsync(MessageRow message, string? type)
    {
        await using var db = postgres.CreateDbContext();
        var recorder = new DecisionRecorder(
            db, settings, new RecordingEmbeddingQueue(), new FakeTimeProvider(Now), NullLogger<DecisionRecorder>.Instance);
        var suggestion = new SuggestionRow
        {
            Id = Guid.NewGuid(),
            MessageId = message.Id,
            SenderAddress = message.FromAddress,
            Source = SuggestionSource.Llm,
            TopicLabel = "Shopping",
            DocumentTypeLabel = type,
            Confidence = 0.9,
            Reason = "Synthetic reason",
            CreatedAt = Now,
        };
        await recorder.RecordAsync(suggestion, message, DecisionOutcome.Approved, Ct);
        await db.SaveChangesAsync(Ct);
    }

    private DecisionMemory Memory(AppDbContext db) =>
        new(db, new FakeLlmClientFactory(embed: new FakeEmbeddingGenerator()), settings, NullLogger<DecisionMemory>.Instance);

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

    private static DecisionRow Approval(int days, string? type, bool decided) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = $"old-{Guid.NewGuid():N}",
        SenderAddress = Shop,
        SubjectTemplate = SubjectNormaliser.Template(Message("s0").Subject),
        ScopeKey = Scope,
        TopicLabel = "Shopping",
        DocumentTypeLabel = type,
        DocumentTypeDecided = decided,
        Outcome = DecisionOutcome.Approved,
        Source = SuggestionSource.Llm,
        CreatedAt = Now.AddDays(days),
    };
}
