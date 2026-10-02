using System.Collections.Concurrent;
using GmailOrganiser.Analysis;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Gmail;
using GmailOrganiser.Gmail.Fake;
using GmailOrganiser.Settings;
using GmailOrganiser.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GmailOrganiser.Tests.Integration;

/// <summary>The billing group's PDFs reach the prompt within the rules (#74) and are never stored or logged.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalysisAttachmentRunTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    /// <summary>Only the synthetic PDFs carry this text.</summary>
    private const string PdfText = "Synthetic statement for example.com, reference QUILLWORT-7731.";

    private readonly PostgresFixture postgres;
    private readonly CapturingLoggerProvider logs = new();
    private readonly FailingPdfConverter failing = new();
    private readonly AnalysisRunHarness h;

    public AnalysisAttachmentRunTests(ApiFactory factory, PostgresFixture postgres)
    {
        this.postgres = postgres;
        h = new AnalysisRunHarness(factory, postgres)
        {
            Customise = m => m.From == AnalysisRunHarness.Billing ? m with { Attachments = [Pdf(m.Id)] } : m,
            ConfigureServices = services =>
            {
                services.Insert(0, ServiceDescriptor.Singleton<IAttachmentConverter>(failing));
                services.AddSingleton<ILoggerProvider>(logs);
                services.Configure<LoggerFilterOptions>(o =>
                    o.Rules.Add(new LoggerFilterRule(typeof(CapturingLoggerProvider).FullName, null, LogLevel.Trace, null)));
            },
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => h.InitializeAsync();

    public ValueTask DisposeAsync() => h.DisposeAsync();

    [Fact]
    public async Task Pdf_text_reaches_the_model_after_the_bodies_and_nowhere_else()
    {
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        done.Status.ShouldBe("completed");
        var prompts = h.Chat.Requests.Select(r => r[^1].Text).ToList();
        var billing = prompts.Where(p => p.Contains("from: " + AnalysisRunHarness.Billing, StringComparison.Ordinal)).ToList();
        billing.ShouldNotBeEmpty();
        var blocks = billing.Sum(p => p.Split('\n').Count(l => l.StartsWith("### Attachment: statement-c0", StringComparison.Ordinal)));
        blocks.ShouldBe(billing.Sum(p => p.Split('\n').Count(l => l.StartsWith("id: c0", StringComparison.Ordinal))));
        (done.AttachmentsConverted, done.AttachmentsSkipped).ShouldBe((blocks, 0));
        foreach (var prompt in billing)
        {
            prompt.ShouldContain(PdfText);
            prompt.IndexOf(AttachmentPromptSection.Heading, StringComparison.Ordinal)
                .ShouldBeGreaterThan(prompt.LastIndexOf("</email_body>", StringComparison.Ordinal));
        }

        prompts.Except(billing).ShouldAllBe(p => !p.Contains("Attachment"));
        h.Chat.Requests[0][0].Text.ShouldContain("converted to text");

        // Only messages with attachments cost the combined fetch.
        h.Gmail.ContentCalls.ShouldAllBe(id => id.StartsWith('c'));

        (await AttachmentConversionStorageTests.TablesContainingAsync(postgres, "QUILLWORT")).ShouldBeEmpty();
        logs.Messages.ShouldNotBeEmpty();
        logs.Messages.ShouldAllBe(m => !m.Contains("QUILLWORT"));
    }

    [Fact]
    public async Task Master_switch_off_leaves_out_the_whole_attachment_block()
    {
        await using (var scope = h.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsStore>()
                .UpdateAsync(s => s with { Attachments = s.Attachments with { Enabled = false } }, Ct);
        }

        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.AttachmentsConverted, done.AttachmentsSkipped).ShouldBe(("completed", 0, 0));
        h.Chat.Requests.Select(r => r[^1].Text).ShouldAllBe(p => !p.Contains(AttachmentPromptSection.Heading));
        h.Gmail.ContentCalls.ShouldBeEmpty();
        h.Gmail.AttachmentContentCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_converter_lists_the_attachments_as_skipped_and_the_run_completes()
    {
        failing.Fail = true;
        var run = await h.StartAsync(new StartAnalysisRunRequest("inbox", null, null, 20, null));

        await h.RunNextAsync();

        var done = await h.GetRunAsync(run.Id);
        (done.Status, done.MessagesCovered, done.FailedMessages, done.AttachmentsConverted).ShouldBe(("completed", 20, 0, 0));
        done.AttachmentsSkipped.ShouldBeGreaterThan(0);
        var billing = h.Chat.Requests.Select(r => r[^1].Text).Where(p => p.Contains("Skipped attachment", StringComparison.Ordinal)).ToList();
        billing.ShouldNotBeEmpty();
        billing.ShouldAllBe(p => p.Contains("(pdf, could not be read)") && !p.Contains(PdfText));
        await using var db = postgres.CreateDbContext();
        (await db.Suggestions.CountAsync(s => s.SenderAddress == AnalysisRunHarness.Billing, Ct)).ShouldBe(4);
    }

    private static FakeAttachment Pdf(string messageId)
    {
        var content = SyntheticPdf.Create("Statement - Example", PdfText);
        return new FakeAttachment($"att-{messageId}", $"statement-{messageId}.pdf", "application/pdf", content.Length, content);
    }

    /// <summary>Takes PDFs over from the real converter while <see cref="Fail"/> is set, and throws.</summary>
    private sealed class FailingPdfConverter : IAttachmentConverter
    {
        public bool Fail { get; set; }

        public bool CanConvert(AttachmentType type) => Fail && type == AttachmentType.Pdf;

        public Task<ConvertedAttachment> ConvertAsync(GmailAttachment attachment, Stream content, ConversionLimits limits, CancellationToken ct) =>
            throw new InvalidOperationException("Synthetic converter failure");
    }

    /// <summary>Every log line, formatted with its values, at every level.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();

        public IReadOnlyList<string> Messages => [.. messages];

        public ILogger CreateLogger(string categoryName) => new Logger(messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception) + " " + state + " " + exception);
        }
    }
}
