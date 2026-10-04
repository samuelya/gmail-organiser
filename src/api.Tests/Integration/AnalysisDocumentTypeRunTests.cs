using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Prompts;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Document-type labels (#239): with a parent set, the model's <c>documentTypeLabel</c> is stored as the existing label
/// is spelled (else in the parent's configured casing), derived members get the agreed value, a label missing from the
/// label tree is marked new, and the run keeps the parent it started with. The prompt (#240) names the parent and its
/// children, or says document types are off.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisDocumentTypeRunTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Parent = "Types";
    private const string Existing = "Types/Invoice";
    private readonly AnalysisRunHarness h = new(factory, postgres);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await h.InitializeAsync();
        await h.Gmail.Inner.CreateLabelAsync(Existing, Ct);
        await SetParentAsync(Parent);
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Model_and_derived_rows_store_the_document_type_label_and_whether_it_is_new()
    {
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(Answer(ids));
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        await AssertRowsAsync();
        await using var db = postgres.CreateDbContext();
        (await db.AnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id, Ct)).DocumentTypeParent.ShouldBe(Parent);
        h.Chat.Requests.ShouldAllBe(r => r[0].Text.Contains($"Document-type labels live under `{Parent}`. Existing: `{Existing}`."));
    }

    [Fact]
    public async Task Without_a_parent_the_prompt_switches_document_types_off_and_none_are_stored()
    {
        await SetParentAsync(null);
        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(Answer(ids));
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();

        h.Chat.Requests.ShouldNotBeEmpty();
        h.Chat.Requests.ShouldAllBe(r => r[0].Text.Contains(AnalysisPromptBuilder.DocumentTypesOff));
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.AsNoTracking().ToListAsync(Ct)).ShouldAllBe(s => s.DocumentTypeLabel == null && !s.DocumentTypeIsNew);
    }

    [Fact]
    public async Task Resumed_run_keeps_the_parent_it_started_with()
    {
        using var stop = new CancellationTokenSource();
        h.Chat.Respond = async (ids, call, _, ct) =>
        {
            if (call == 2)
            {
                // The parent is cleared and the host stops while the second group waits for the model.
                await SetParentAsync(null);
                stop.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Answer(ids);
        };
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync(stop.Token);
        (await h.Runner.RecoverAsync(Ct)).ShouldBe(1);
        await h.RunNextAsync();

        await AssertRowsAsync();
    }

    /// <summary>The model names an existing type in other casing, a new type, and no type for the third sender.</summary>
    private static string Answer(IEnumerable<string> ids) => JsonSerializer.Serialize(new
    {
        suggestions = ids.Select(id => new
        {
            id,
            topicLabel = AnalysisRunHarness.LabelFor(id),
            isNewLabel = false,
            needsAction = false,
            toBeDeleted = false,
            unsubscribeSuggested = false,
            confidence = 0.9,
            reason = "Synthetic reason",
            documentTypeLabel = id[0] switch
            {
                'c' => "types/invoice",
                'a' => "TYPES/Receipt",
                _ => null,
            },
        }),
    });

    private async Task SetParentAsync(string? parent)
    {
        using var scope = h.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { DocumentTypeParent = parent }, Ct);
    }

    /// <summary>An existing type takes the label tree's spelling; a new one keeps the configured parent casing.</summary>
    private async Task AssertRowsAsync()
    {
        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().ToListAsync(Ct);
        rows.ShouldContain(s => s.Source == SuggestionSource.Derived && s.SenderAddress == AnalysisRunHarness.Billing);
        rows.ShouldContain(s => s.Source == SuggestionSource.Derived && s.SenderAddress == AnalysisRunHarness.Shop);

        rows.Where(s => s.SenderAddress == AnalysisRunHarness.Billing)
            .ShouldAllBe(s => s.DocumentTypeLabel == Existing && !s.DocumentTypeIsNew);
        rows.Where(s => s.SenderAddress == AnalysisRunHarness.Shop)
            .ShouldAllBe(s => s.DocumentTypeLabel == "Types/Receipt" && s.DocumentTypeIsNew);
        rows.Where(s => s.SenderAddress == AnalysisRunHarness.News)
            .ShouldAllBe(s => s.DocumentTypeLabel == null && !s.DocumentTypeIsNew);
    }
}
