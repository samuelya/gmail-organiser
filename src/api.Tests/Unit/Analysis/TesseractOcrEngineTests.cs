using System.Runtime.Versioning;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Tests.Fakes;
using static GmailOrganiser.Tests.Unit.Analysis.ImageTestSupport;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>
/// <see cref="TesseractOcrEngine"/>: how it finds its binary, and the real Tesseract on rendered synthetic images where
/// it is installed (CI and the api image).
/// </summary>
public sealed class TesseractOcrEngineTests
{
    private const string NotInstalled = "Tesseract is not installed (CI and the api image have it).";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reads_rendered_synthetic_text(bool jpeg)
    {
        Assert.SkipUnless(TesseractInstalled.Value, NotInstalled);

        var text = await Tesseract().ReadTextAsync(jpeg ? SyntheticImage.Jpeg : SyntheticImage.Png, Ct);

        foreach (var line in SyntheticImage.Lines)
        {
            text.ShouldContain(line);
        }
    }

    [Fact]
    public async Task Reads_a_scanned_pdf_end_to_end()
    {
        Assert.SkipUnless(TesseractInstalled.Value, NotInstalled);
        var scan = SyntheticImage.ScannedPdf(1);

        var digest = await ConvertAllAsync([Att("scan.pdf", "application/pdf", scan)], Limits(Ocr), Tesseract());

        digest.Converted.Single().Markdown.ShouldContain("billing@example.com");
    }

    [Fact]
    public async Task A_configured_full_path_starts_the_engine_like_a_name_on_path()
    {
        Assert.SkipUnless(TesseractInstalled.Value, NotInstalled);
        var full = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => (string[])[Path.Combine(directory, "tesseract"), Path.Combine(directory, "tesseract.exe")])
            .First(File.Exists);

        var text = await Tesseract(path: full).ReadTextAsync(SyntheticImage.Png, Ct);

        text.ShouldContain(SyntheticImage.Lines[0]);
    }

    /// <summary>
    /// The OS bound behind the header check: a 39.7 MP RGB PNG passes the 40 MP cap, yet under a 256 MiB address-space
    /// limit Leptonica can't allocate its canvas (measured: fails at 384 MiB, reads at 512 MiB), so Tesseract exits
    /// non-zero and the image is a failed attachment, while the next image reads normally in a fresh process.
    /// </summary>
    [Fact]
    public async Task Under_the_memory_limit_fails_an_image_the_header_check_let_through_and_keeps_reading()
    {
        Assert.SkipUnless(TesseractInstalled.Value && OperatingSystem.IsLinux(), "Needs Tesseract and prlimit (Linux: CI and the api image).");
        var engine = Tesseract(memoryLimitMb: 256);
        var big = SyntheticImage.BlankPng(6_300, 6_300);
        ImageHeader.DeclaredPixels(big).ShouldBe(6_300L * 6_300);

        var digest = await ConvertAllAsync([Att("big.png", "image/png", big), Att("small.png", "image/png", Png)], Limits(Ocr), engine);

        digest.Skipped.ShouldBe([new SkippedAttachment("big.png", AttachmentType.Image, SkipReason.Failed)]);
        digest.Converted.Single().Markdown.ShouldContain("billing@example.com");
        (await Should.ThrowAsync<InvalidOperationException>(() => engine.ReadTextAsync(big, Ct))).Message.ShouldContain("exited with code");
    }

    [Fact]
    public async Task Missing_engine_fails_the_image_and_leaves_a_scanned_pdf_unread_not_failed()
    {
        var engine = Tesseract(path: "tesseract-not-installed-example");

        await Should.ThrowAsync<OcrUnavailableException>(() => engine.ReadTextAsync(Png, Ct));
        var digest = await ConvertAllAsync(
            [Att("photo.png", "image/png", Png), Att("scan.pdf", "application/pdf", SyntheticImage.ScannedPdf(2))], Limits(Ocr), engine);
        digest.Skipped.Single().ShouldBe(new SkippedAttachment("photo.png", AttachmentType.Image, SkipReason.Failed));
        digest.Converted.Single().Markdown.ShouldBeEmpty();
    }

    [Fact]
    public void A_configured_path_that_is_missing_a_directory_or_not_executable_is_an_unavailable_engine()
    {
        using var directory = new TempDirectory();
        var missing = Path.Combine(directory.Path, "tesseract");
        var plain = Path.Combine(directory.Path, "tesseract.txt");
        File.WriteAllText(plain, "not a program");

        Should.Throw<OcrUnavailableException>(() => Tesseract(path: missing).EnsureAvailable());
        Should.Throw<OcrUnavailableException>(() => Tesseract(path: directory.Path).EnsureAvailable());
        if (!OperatingSystem.IsWindows())
        {
            // Exists but can't run: prlimit would otherwise report exit 126 and fail each page instead of leaving the PDF unread.
            File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Should.Throw<OcrUnavailableException>(() => Tesseract(path: plain).EnsureAvailable());
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task The_binary_is_looked_up_once_and_a_stand_in_script_runs_under_the_same_arguments()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as the engine.");
        using var directory = new TempDirectory();
        var script = Path.Combine(directory.Path, "tesseract");
        File.WriteAllText(script, "#!/bin/sh\ncat > /dev/null\necho \"read $1 $2 $3 $4\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var engine = Tesseract(path: script);

        var text = await engine.ReadTextAsync(Png, Ct);
        // Resolved once: the cached path is kept after the file stops being executable, while a new engine looks again.
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        text.ShouldBe("read stdin stdout -l eng\n");
        Should.NotThrow(engine.EnsureAvailable);
        Should.Throw<OcrUnavailableException>(() => Tesseract(path: script).EnsureAvailable());
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("gmo-ocr-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
