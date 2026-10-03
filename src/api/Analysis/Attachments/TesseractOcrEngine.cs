using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// OCR with the Tesseract CLI (Apache-2.0), installed in the api image; for IDE runs install it on the host or set
/// <see cref="AttachmentOptions.TesseractPath"/>. The image goes in on stdin and the text comes back on stdout, so
/// nothing touches the disk. Leptonica decodes png, jpeg, gif, webp, tiff, bmp and jp2. The process is killed when
/// <c>ct</c> is cancelled (the image timeout). Its stderr is drained, never logged. A missing binary is logged once
/// until the engine starts again.
/// </summary>
public sealed class TesseractOcrEngine(IOptions<AttachmentOptions> options, ILogger<TesseractOcrEngine> logger) : IOcrEngine
{
    private int missingLogged;

    public async Task<string> ReadTextAsync(ReadOnlyMemory<byte> image, CancellationToken ct)
    {
        var o = options.Value;
        var start = new ProcessStartInfo(o.TesseractPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
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
            if (Interlocked.Exchange(ref missingLogged, 1) == 0)
            {
                logger.LogWarning(
                    "The OCR engine {Path} could not be started; install Tesseract or set Attachments:TesseractPath", o.TesseractPath);
            }

            throw new OcrUnavailableException("The OCR engine could not be started.", ex);
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
}
