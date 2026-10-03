using System.Buffers.Binary;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// The pixel count a PNG, JPEG or WebP declares in its header, read without decoding it, so a small file declaring a huge
/// canvas (a decompression bomb) never reaches Ollama, which decodes on the host with no other bound, and is turned away
/// before Tesseract starts. These three formats have one size field their decoders trust; the other formats Tesseract
/// reads (GIF, TIFF, BMP, JPEG 2000) are not parsed here and rely on the OCR process's memory limit
/// (<see cref="AttachmentOptions.OcrMemoryLimitMb"/>), since re-implementing their decoders' header semantics proved
/// to be another way to get it wrong (#158).
/// </summary>
public static class ImageHeader
{
    /// <summary>Declared width × height above which an image or PDF page image is not decoded (an A4 page at 600 dpi is 35 MP).</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>
    /// More JPEG components than grey, RGB, RGBA or CMYK: libjpeg allocates coefficient arrays per component before
    /// Leptonica rejects the image.
    /// </summary>
    private const int MaxComponents = 4;

    /// <summary>
    /// Deeper animation frames nested in animation frames than any decoder reads (libwebp reads none): each level is a
    /// recursive call, so an unbounded chain could exhaust the stack.
    /// </summary>
    private const int MaxWebPFrameDepth = 8;

    /// <summary>Whether <see cref="DeclaredPixels(string?, ReadOnlySpan{byte})"/> reads this format: PNG, JPEG and WebP.</summary>
    public static bool Reads(string? mediaType) => mediaType is "image/png" or "image/jpeg" or "image/webp";

    /// <summary>Sniffs the media type first; <see cref="DeclaredPixels(string?, ReadOnlySpan{byte})"/> when the caller has it.</summary>
    public static long? DeclaredPixels(ReadOnlySpan<byte> image) => DeclaredPixels(ImageTextReader.MediaType(image), image);

    /// <summary>
    /// The declared width × height (the largest where a file could carry more than one size). <c>null</c> when
    /// <see cref="Reads"/> is false for the format, or the header is truncated, malformed or declares no pixels.
    /// </summary>
    /// <param name="mediaType"><see cref="ImageTextReader.MediaType"/> of <paramref name="image"/>.</param>
    public static long? DeclaredPixels(string? mediaType, ReadOnlySpan<byte> image)
    {
        var pixels = mediaType switch
        {
            "image/png" => Png(image),
            "image/jpeg" => Jpeg(image),
            "image/webp" => WebP(image),
            _ => null,
        };
        return pixels > 0 ? pixels : null;
    }

    /// <summary>The first chunk's IHDR; a second IHDR anywhere declares nothing (libpng rejects it, others may not).</summary>
    private static long? Png(ReadOnlySpan<byte> s)
    {
        if (s.Length < 24 || !s[12..16].SequenceEqual("IHDR"u8))
        {
            return null;
        }

        for (long i = 8 + 12 + BinaryPrimitives.ReadUInt32BigEndian(s[8..]); i + 8 <= s.Length; i += 12 + BinaryPrimitives.ReadUInt32BigEndian(s[(int)i..]))
        {
            if (s[(int)(i + 4)..(int)(i + 8)].SequenceEqual("IHDR"u8))
            {
                return null;
            }
        }

        return Area(BinaryPrimitives.ReadUInt32BigEndian(s[16..]), BinaryPrimitives.ReadUInt32BigEndian(s[20..]));
    }

    /// <summary>
    /// The largest frame header (SOF0–SOF15) up to EOI: libjpeg rejects a second one, other decoders may use either. A
    /// frame with more than <see cref="MaxComponents"/> components declares nothing. Segments such as EXIF thumbnails are
    /// skipped by length; stray bytes, 0xFF fill bytes and entropy-coded data are skipped byte by byte, as libjpeg does
    /// when it looks for the next marker.
    /// </summary>
    private static long? Jpeg(ReadOnlySpan<byte> s)
    {
        long largest = 0;
        var i = 2;
        while (i < s.Length)
        {
            if (s[i++] != 0xFF)
            {
                continue;
            }

            while (i < s.Length && s[i] == 0xFF)
            {
                i++;
            }

            if (i >= s.Length)
            {
                break;
            }

            var marker = s[i++];
            if (marker is 0x00 or 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (marker is 0xDA && largest == 0)
            {
                return null;
            }

            if (marker is 0xD9 || i + 2 > s.Length)
            {
                break;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(s[i..]);
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                // Length, precision, height, width, component count.
                if (i + 8 > s.Length || s[i + 7] is 0 or > MaxComponents)
                {
                    return null;
                }

                largest = Math.Max(largest, (long)BinaryPrimitives.ReadUInt16BigEndian(s[(i + 3)..]) * BinaryPrimitives.ReadUInt16BigEndian(s[(i + 5)..]));
            }

            if (length < 2)
            {
                return null;
            }

            i += length;
        }

        return largest;
    }

    /// <summary>
    /// The largest of the VP8X canvas, every VP8/VP8L frame and every animation frame: libwebp rejects a frame that
    /// doesn't fit the canvas, but a decoder that ignores VP8X allocates the frame's own size. Frames nested deeper than
    /// <see cref="MaxWebPFrameDepth"/> declare nothing.
    /// </summary>
    private static long? WebP(ReadOnlySpan<byte> s) =>
        s.Length >= 16 && (s[12..16].SequenceEqual("VP8 "u8) || s[12..16].SequenceEqual("VP8L"u8) || s[12..16].SequenceEqual("VP8X"u8))
            ? WebPChunks(s[12..])
            : null;

    private static long? WebPChunks(ReadOnlySpan<byte> s, int depth = 0)
    {
        if (depth > MaxWebPFrameDepth)
        {
            return null;
        }

        long largest = 0;
        for (var i = 0; i + 8 <= s.Length;)
        {
            var chunk = s[i..(i + 4)];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(s[(i + 4)..]);
            var data = s[(i + 8)..];
            long? pixels = 0;
            if (chunk.SequenceEqual("VP8 "u8))
            {
                pixels = data is [_, _, _, 0x9D, 0x01, 0x2A, _, _, _, _, ..]
                    ? (long)(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) & 0x3FFF) * (BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x3FFF)
                    : null;
            }
            else if (chunk.SequenceEqual("VP8L"u8))
            {
                pixels = data is [0x2F, _, _, _, _, ..]
                    ? (long)((BinaryPrimitives.ReadUInt32LittleEndian(data[1..]) & 0x3FFF) + 1) * (((BinaryPrimitives.ReadUInt32LittleEndian(data[1..]) >> 14) & 0x3FFF) + 1)
                    : null;
            }
            else if (chunk.SequenceEqual("VP8X"u8))
            {
                pixels = data.Length >= 10 ? (long)(UInt24(data[4..]) + 1) * (UInt24(data[7..]) + 1) : null;
            }
            else if (chunk.SequenceEqual("ANMF"u8))
            {
                // X, Y, width - 1, height - 1, duration (24 bits each), flags, then the frame's own chunks.
                var frames = data.Length >= 16 && size >= 16 ? WebPChunks(data[16..(int)Math.Min(size, (uint)data.Length)], depth + 1) : null;
                pixels = frames is { } nested ? Math.Max((long)(UInt24(data[6..]) + 1) * (UInt24(data[9..]) + 1), nested) : null;
            }

            if (pixels is null)
            {
                return null;
            }

            largest = Math.Max(largest, pixels.Value);
            if (size > data.Length)
            {
                break;
            }

            i += 8 + (int)size + (int)(size & 1);
        }

        return largest;

        static int UInt24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;
    }

    /// <summary>Two 32-bit sides can't overflow 64 bits unsigned; saturates instead of turning negative.</summary>
    private static long Area(uint width, uint height) => (long)Math.Min((ulong)width * height, long.MaxValue);
}
