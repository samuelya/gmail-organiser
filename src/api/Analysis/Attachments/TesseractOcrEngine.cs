using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// OCR with the Tesseract CLI (Apache-2.0), installed in the api image; for IDE runs install it on the host or set
/// <see cref="AttachmentOptions.TesseractPath"/>. The image goes in on stdin and the text comes back on stdout, so
/// nothing touches the disk. Leptonica decodes png, jpeg, gif, webp, tiff, bmp and jp2. On Linux the process runs under
/// <c>prlimit --as</c> with <see cref="AttachmentOptions.OcrMemoryLimitMb"/>, so a decoder that wants more than that
/// (a decompression bomb the header check didn't catch) fails inside its own process and the image ends as a failure;
/// a host without <c>prlimit</c> runs OCR unbounded and says so once. The process is killed when <c>ct</c> is cancelled
/// (the image timeout). Its stderr is drained, never logged. A missing binary is logged once until the engine starts
/// again.
/// </summary>
public sealed class TesseractOcrEngine(IOptions<AttachmentOptions> options, ILogger<TesseractOcrEngine> logger) : IOcrEngine
{
    /// <summary>The <c>prlimit</c> binary (util-linux, in every Debian and Ubuntu base image); <c>null</c> off Linux or without it.</summary>
    private static readonly Lazy<string?> Prlimit = new(() => OperatingSystem.IsLinux() ? Find("prlimit") : null);

    /// <summary>The Tesseract binary once found; a miss is looked up again, since it may be installed while the api runs.</summary>
    private string? tesseractPath;

    private int missingLogged;
    private int unboundedLogged;

    public void EnsureAvailable() => Resolve(options.Value);

    public async Task<string> ReadTextAsync(ReadOnlyMemory<byte> image, CancellationToken ct)
    {
        var o = options.Value;
        var tesseract = Resolve(o);
        var start = new ProcessStartInfo(Prlimit.Value ?? tesseract)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (Prlimit.Value is not null)
        {
            // prlimit sets its own limit and execs the command, so it is the same process Tesseract then runs in.
            start.ArgumentList.Add($"--as={(long)o.OcrMemoryLimitMb * 1024 * 1024}");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(tesseract);
        }
        else if (Interlocked.Exchange(ref unboundedLogged, 1) == 0)
        {
            logger.LogWarning(
                "prlimit is not available on this host; OCR runs without the {LimitMb} MiB memory limit (Attachments:OcrMemoryLimitMb)",
                o.OcrMemoryLimitMb);
        }

        foreach (var argument in (string[])["stdin", "stdout", "-l", o.OcrLanguages])
        {
            start.ArgumentList.Add(argument);
        }

        // One thread per page is faster than OpenMP's spin-waiting on a shared CPU (measured on #72).
        start.Environment["OMP_THREAD_LIMIT"] = "1";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("The OCR engine did not start.");
        }
        catch (Win32Exception ex)
        {
            tesseractPath = null;
            throw Unavailable(o, ex);
        }

        Interlocked.Exchange(ref missingLogged, 0);

        using (process)
        {
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(ct);
                var errors = process.StandardError.ReadToEndAsync(ct);
                await process.StandardInput.BaseStream.WriteAsync(image, ct);
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct);
                var text = await output;
                await errors;
                return process.ExitCode == 0
                    ? text
                    : throw new InvalidOperationException($"The OCR engine exited with code {process.ExitCode}.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
    }

    /// <summary>
    /// The Tesseract executable, found once and kept: the configured path, or the configured name on <c>PATH</c>. Looked up
    /// here rather than left to the OS, since prlimit would report a missing or non-executable command as an ordinary
    /// failure (exit 126/127) instead of an engine that isn't there.
    /// </summary>
    private string Resolve(AttachmentOptions o) => tesseractPath ??= Find(o.TesseractPath) ?? throw Unavailable(o, null);

    private OcrUnavailableException Unavailable(AttachmentOptions o, Exception? inner)
    {
        if (Interlocked.Exchange(ref missingLogged, 1) == 0)
        {
            logger.LogWarning(
                "The OCR engine {Path} could not be started; install Tesseract or set Attachments:TesseractPath", o.TesseractPath);
        }

        return new OcrUnavailableException("The OCR engine could not be started.", inner);
    }

    /// <summary>
    /// The executable file <paramref name="name"/> refers to, as <c>CreateProcess</c>/<c>execvp</c> would find it: the file
    /// itself when the name has a directory, else the first match in <c>PATH</c>, with <c>.exe</c> appended as well on Windows.
    /// </summary>
    private static string? Find(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? (string[])["", ".exe"] : [""];
        var directories = name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)
            ? [""]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return directories
            .SelectMany(directory => extensions.Select(extension => Path.Combine(directory, name + extension)))
            .FirstOrDefault(IsExecutableFile);
    }

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        const UnixFileMode AnyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & AnyExecute) != 0;
    }
}
