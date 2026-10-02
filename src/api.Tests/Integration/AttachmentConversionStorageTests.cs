using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Jobs;
using GmailOrganiser.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GmailOrganiser.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class AttachmentConversionStorageTests(ApiFactory factory, PostgresFixture postgres) : IClassFixture<ApiFactory>
{
    /// <summary>Only the seeded PDFs carry this text; see <c>FakeMailboxSeed</c>.</summary>
    private const string PdfText = "Synthetic invoice for example.com testing.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Conversion_through_the_app_returns_pdf_text_and_leaves_none_in_any_table()
    {
        await using var host = factory.WithWebHostBuilder(b => b.UseSetting("GMAIL_FAKE", "true").ConfigureTestServices(RemoveRunner));
        _ = host.Server;
        await using var scope = host.Services.CreateAsyncScope();
        var fake = scope.ServiceProvider.GetRequiredService<FakeGmailClient>();
        var service = scope.ServiceProvider.GetRequiredService<AttachmentConversionService>();
        var message = fake.Messages.First(m => m.Attachments.Any(a => a.Filename.StartsWith("invoice", StringComparison.Ordinal)));

        var digest = await service.ConvertAllAsync(
            message.Id, new HashSet<AttachmentType> { AttachmentType.Pdf }, ConversionLimits.Default, Ct);

        digest.Skipped.ShouldBeEmpty();
        digest.Converted.Single().Markdown.ShouldContain(PdfText);
        (await TablesContainingAsync(PdfText)).ShouldBeEmpty();
    }

    /// <summary>Every public table whose rows, rendered as text, contain <paramref name="text"/>.</summary>
    private async Task<List<string>> TablesContainingAsync(string text)
    {
        await using var db = postgres.CreateDbContext();
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'")
            .ToListAsync(Ct);
        tables.ShouldNotBeEmpty();
        var hits = new List<string>();
        foreach (var table in tables)
        {
            // Table names come from the catalog, not from input; the searched text is a parameter.
#pragma warning disable EF1002
            var count = await db.Database
                .SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM \"{table}\" t WHERE t::text LIKE '%' || {{0}} || '%'", text)
                .SingleAsync(Ct);
#pragma warning restore EF1002
            if (count > 0)
            {
                hits.Add(table);
            }
        }

        return hits;
    }

    private static void RemoveRunner(IServiceCollection services) =>
        services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobRunner)));
}
