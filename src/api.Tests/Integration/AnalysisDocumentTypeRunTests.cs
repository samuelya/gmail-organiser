using System.Text.Json;
using GmailOrganiser.Analysis;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GmailOrganiser.Tests.Integration;

/// <summary>
/// Document-type labels (#239): with a parent set, the model's <c>documentTypeLabel</c> is stored in the parent's
/// configured casing, derived members get the agreed value, and a label missing from the label tree is marked new.
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
        using (var scope = h.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(x => x with { DocumentTypeParent = Parent }, Ct);
        }

        h.Chat.Respond = (ids, _, _, _) => Task.FromResult(JsonSerializer.Serialize(new
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
        }));
        await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));
        await h.RunNextAsync();
    }

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Model_and_derived_rows_store_the_document_type_label_and_whether_it_is_new()
    {
        await using var db = postgres.CreateDbContext();
        var rows = await db.Suggestions.AsNoTracking().ToListAsync(Ct);
        rows.ShouldContain(s => s.Source == SuggestionSource.Derived && s.SenderAddress == AnalysisRunHarness.Billing);
        rows.ShouldContain(s => s.Source == SuggestionSource.Derived && s.SenderAddress == AnalysisRunHarness.Shop);

        rows.Where(s => s.SenderAddress == AnalysisRunHarness.Billing)
            .ShouldAllBe(s => s.DocumentTypeLabel == "Types/invoice" && !s.DocumentTypeIsNew);
        rows.Where(s => s.SenderAddress == AnalysisRunHarness.Shop)
            .ShouldAllBe(s => s.DocumentTypeLabel == "Types/Receipt" && s.DocumentTypeIsNew);
        rows.Where(s => s.SenderAddress == AnalysisRunHarness.News)
            .ShouldAllBe(s => s.DocumentTypeLabel == null && !s.DocumentTypeIsNew);
    }
}
