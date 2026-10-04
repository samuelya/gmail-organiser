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
    [InlineData(Parent, null, Parent, true)]
    [InlineData(null, Parent, null, false)]
    [InlineData(Parent, "Other", Parent, true)]
    public async Task Recorder_takes_the_parent_from_the_suggestions_run_not_the_current_settings(
        string? runParent, string? settingsParent, string? recorded, bool decided)
    {
        settings.Current = settings.Current with { DocumentTypeParent = settingsParent };
        await RecordAsync(Message("m1"), "Type/Invoice", await RunAsync(runParent));

        await using var db = postgres.CreateDbContext();
        var row = await db.Decisions.AsNoTracking().SingleAsync(Ct);
        (row.DocumentTypeLabel, row.DocumentTypeParent, row.DocumentTypeDecided).ShouldBe(("Type/Invoice", recorded, decided));
    }

    [Fact]
    public async Task Recorder_without_a_run_records_no_parent()
    {
        settings.Current = settings.Current with { DocumentTypeParent = Parent };
        await RecordAsync(Message("m1"), "Type/Invoice", runId: null);

        await using var db = postgres.CreateDbContext();
        var row = await db.Decisions.AsNoTracking().SingleAsync(Ct);
        (row.DocumentTypeParent, row.DocumentTypeDecided).ShouldBe((null, false));
    }

    [Theory]
    [InlineData("Type/Invoice", "type/invoice", true)]
    [InlineData(null, null, true)]
    [InlineData("Type/Invoice", null, false)]
    [InlineData("Type/Invoice", "Type/Receipt", false)]
    public async Task Approvals_under_the_parent_must_agree_on_the_type_label(string? latest, string? older, bool agrees)
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(Approval(0, latest, Parent), Approval(-1, older, Parent), Approval(-2, older, Parent));
        await db.SaveChangesAsync(Ct);

        var patterns = await Memory(db).FindPatternsAsync([Scope], minApprovals: 3, Parent, Ct);

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
            db.Decisions.AddRange(Enumerable.Range(1, 20).Select(i => Approval(-i, type: null, parent: null)));
            await db.SaveChangesAsync(Ct);
        }

        // Parent off: the old behaviour, no type.
        (await CoverAsync(parent: null)).ShouldNotBeNull().Suggestions.Single().DocumentTypeLabel.ShouldBeNull();

        // Parent on: the latest approval was not decided under it, so the group goes to the model.
        (await CoverAsync(Parent)).ShouldBeNull();

        // One approval of a run made with the parent set; the 20 old ones do not contradict it.
        await RecordAsync(Message("m1"), "Type/Invoice", await RunAsync(Parent));
        var covered = (await CoverAsync(Parent)).ShouldNotBeNull().Suggestions.Single();
        covered.DocumentTypeLabel.ShouldBe("Type/Invoice");

        // Turning the parent off again restores the old answer.
        (await CoverAsync(parent: null)).ShouldNotBeNull().Suggestions.Single().DocumentTypeLabel.ShouldBeNull();
    }

    [Fact]
    public async Task A_changed_parent_relearns_the_type_after_one_approval()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(Enumerable.Range(1, 20).Select(i => Approval(-i, "Old/Invoice", "Old")));
            await db.SaveChangesAsync(Ct);
        }

        (await CoverAsync("Old")).ShouldNotBeNull().Suggestions.Single().DocumentTypeLabel.ShouldBe("Old/Invoice");

        // Under the new parent the old approvals neither answer nor contradict the first one made under it.
        (await CoverAsync(Parent)).ShouldBeNull();
        await RecordAsync(Message("m1"), "Type/Invoice", await RunAsync(Parent));
        (await CoverAsync(Parent)).ShouldNotBeNull().Suggestions.Single().DocumentTypeLabel.ShouldBe("Type/Invoice");
    }

    [Fact]
    public async Task Memory_answers_with_the_type_spelled_as_the_parser_stores_it()
    {
        await using (var db = postgres.CreateDbContext())
        {
            db.Decisions.AddRange(Enumerable.Range(1, 3).Select(i => Approval(-i, "type/invoice", "TYPE")));
            await db.SaveChangesAsync(Ct);
        }

        (await CoverAsync(Parent)).ShouldNotBeNull().Suggestions.Single().DocumentTypeLabel.ShouldBe("Type/invoice");
    }

    [Fact]
    public async Task Similar_decision_hints_carry_the_type_label_and_whether_it_was_decided_under_the_parent()
    {
        await using var db = postgres.CreateDbContext();
        db.Decisions.AddRange(
            Approval(0, "Type/Invoice", Parent), Approval(-1, type: null, Parent), Approval(-2, "Old/Invoice", "Old"),
            Approval(-3, type: null, parent: null));
        await db.SaveChangesAsync(Ct);

        var hints = await Memory(db).FindSimilarAsync([Message("m9")], vectors: null, k: 5, Parent, Ct);

        hints.Select(h => (h.DocumentTypeLabel, h.DocumentTypeDecided))
            .ShouldBe([("Type/Invoice", true), (null, true), ("Old/Invoice", false)]);
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

    private async Task<Guid> RunAsync(string? parent)
    {
        await using var db = postgres.CreateDbContext();
        var run = new AnalysisRunRow { Id = Guid.NewGuid(), Scope = AnalysisScope.Inbox, DocumentTypeParent = parent, CreatedAt = Now };
        db.AnalysisRuns.Add(run);
        await db.SaveChangesAsync(Ct);
        return run.Id;
    }

    private async Task RecordAsync(MessageRow message, string? type, Guid? runId)
    {
        await using var db = postgres.CreateDbContext();
        var recorder = new DecisionRecorder(
            db, new RecordingEmbeddingQueue(), new FakeTimeProvider(Now), NullLogger<DecisionRecorder>.Instance);
        var suggestion = new SuggestionRow
        {
            Id = Guid.NewGuid(),
            RunId = runId,
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

    private static DecisionRow Approval(int days, string? type, string? parent) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = $"old-{Guid.NewGuid():N}",
        SenderAddress = Shop,
        SubjectTemplate = SubjectNormaliser.Template(Message("s0").Subject),
        ScopeKey = Scope,
        TopicLabel = "Shopping",
        DocumentTypeLabel = type,
        DocumentTypeDecided = parent is not null,
        DocumentTypeParent = parent,
        Outcome = DecisionOutcome.Approved,
        Source = SuggestionSource.Llm,
        CreatedAt = Now.AddDays(days),
    };
}
