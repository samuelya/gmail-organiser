using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace GmailOrganiser.Tests.Fakes;

/// <summary>
/// Synthetic images of <see cref="Lines"/> (black on white, 420x130 grayscale) and scanned-style PDFs built from them,
/// so no photos or scans are checked in. The bytes were rendered once with SkiaSharp (MIT) on macOS; a native renderer
/// in the test run crashed the test host on Linux. To regenerate: an <c>SKSurface</c> of
/// <c>new SKImageInfo(420, 130, SKColorType.Gray8, SKAlphaType.Opaque)</c>, cleared white, each line drawn with
/// <c>SKTypeface.Default</c> at size 24 at (20, 38 + 36 * i), encoded as PNG (quality 100) and JPEG (quality 90).
/// </summary>
public static class SyntheticImage
{
    public static readonly string[] Lines = ["INVOICE 2026-0042", "billing@example.com", "Total due: 123.45 EUR"];

    public static byte[] Png => Convert.FromBase64String(PngBase64);

    public static byte[] Jpeg => Convert.FromBase64String(JpegBase64);

    /// <summary>One page per image; JPEG is embedded as stored, PNG as decoded pixels (as many scanners write them).</summary>
    public static byte[] ScannedPdf(int pages, bool asJpeg = true)
    {
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pages; i++)
        {
            var page = builder.AddPage(612, 792);
            var area = new PdfRectangle(36, 600, 456, 730);
            if (asJpeg)
            {
                page.AddJpeg(Jpeg, area);
            }
            else
            {
                page.AddPng(Png, area);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// A valid RGB PNG of <paramref name="width"/> × <paramref name="height"/> black pixels: a few hundred KB however large,
    /// since the rows deflate to almost nothing, while its decoder allocates the whole canvas. Not a header-only stub:
    /// Leptonica reads it to the end when it has the memory.
    /// </summary>
    public static byte[] BlankPng(int width, int height)
    {
        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + (3 * width)];
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        byte[] ihdr = [.. Be32((uint)width), .. Be32((uint)height), 8, 2, 0, 0, 0];
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", idat.ToArray()), .. Chunk("IEND", [])];

        static byte[] Chunk(string type, byte[] data)
        {
            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
            return [.. Be32((uint)data.Length), .. typed, .. Be32(Crc32(typed))];
        }

        static byte[] Be32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return bytes;
        }

        // libpng rejects a critical chunk whose CRC is wrong, so the chunks carry real ones.
        static uint Crc32(ReadOnlySpan<byte> bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return ~crc;
        }
    }

    /// <summary>
    /// A one-page PDF whose only image declares <paramref name="width"/> x <paramref name="height"/> 8-bit gray samples and
    /// stores <paramref name="data"/> as is under <paramref name="filter"/>: declared sizes and encodings PdfPig's writer
    /// can't produce (a decompression bomb, JPEG 2000).
    /// </summary>
    public static byte[] PdfWithImage(int width, int height, string filter, byte[] data)
    {
        const string content = "q 420 0 0 130 36 600 cm /Im1 Do Q";
        var pdf = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text) => pdf.Write(Encoding.Latin1.GetBytes(text));
        void Object(string body, byte[]? stream = null)
        {
            offsets.Add(pdf.Position);
            Write($"{offsets.Count} 0 obj\n{body}\n");
            if (stream is not null)
            {
                Write("stream\n");
                pdf.Write(stream);
                Write("\nendstream\n");
            }

            Write("endobj\n");
        }

        Write("%PDF-1.7\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>");
        Object($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /{filter} /Length {data.Length} >>", data);
        Object($"<< /Length {content.Length} >>", Encoding.Latin1.GetBytes(content));
        var xref = pdf.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    private const string PngBase64 =
        "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";

    private const string JpegBase64 =
        "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" +
        "TjLAN02lTJGoZFxZLyinbaqLzaWKk32GEzMqXDtHLkwEglQTpS9K3xj2W3LZxzZlCzl/0TUzAtj0SdRO12Ym3JGoT1X4+RKsBLX0IJGiv6TpXI" +
        "K2kJOhJe48pK6hZmiu2pTkYjTRw+zcgmEerXUOSQWC33uXHRUd9rXj80WhGYLwwzeNV+IXYuTJWj92yKZZz9Km6p6pkduaU7MqS32ivuHw4j6g" +
        "kp8+/gxE/iL4tyrmOXx1QLIsd6+rOlai5U7mpKK5L0tE8Gy36eXWtxxKik7dJ4g6ISfBAI5GOsndSVtfIbclelGjWparDrUsoU+8acGpKXKgFr" +
        "Q0g+eKSVcQNnX6mI3bv9YrpMv+/wClUjG83nCwbgrLtaos+1cKJeapxd8GWcD3M8UhKB4SEjRUCeRSnvOnjD2YbKOdM1XLQKUzlm95cPUezmJp" +
        "K5eXMuwsSzDzwWlBUtXbSohYACeRUCohNqzeDad1R4is9HUHYUibmlQqamaNKT7qWZKYVtJ4LYfPIFAT/GoeYzblL4YVgr6gMRrs/GLYxok1D9" +
        "sQK08BrtJ9JsOTHdP4nL9z/wA3iLIvOXz706ZjnKpYVqu5bw/P02Vk5O1G6s3KTNAcZbS3pou7K0KCSr+Inlo8eIKvZ07Ygybe3UvcWfstW/KW" +
        "ROuUZNvW/ajE6iddlJbmFqcedR9JUTy1o7/EXtKdJ3e/UbadVvzp+yVbVClfXVqsW5UJCRle4hvvPuyy0No5LISnalAbUQBvyRGSMH3Z1c4UxJ" +
        "atisdNVPqrNBkkSSJ1y86e2p4J39RSHTr39tmNBYzqGT872Nfts5pxaxjeQqMgaYwiUrkvUjOszDTrcx5aUe2UAo1v35+PYxnjDf8AWe6NrM/o" +
        "skMOSuX7bps0+i37kp9fYkCWXHFOAPtOclDSlqPniE74hSgAYtbow6e76sm68iZZywqTYyNfsw0p+k09YcZpks1sIaC0qUFEjgPBUAG0fUSVR7" +
        "+jLC15YnyD1B1K6qP8rkrqvebq9Hd9Uy96qUW68pLmm1qKNhaTxWEq8+0cTq9w3fN6586dL6s22zcMpZtZmna0lqclpdxqWeVLDkO84jnoNukJ" +
        "Ts/9Y1fCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEI" +
        "QhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIQhCEIR/9k=";
}
