using System.Buffers.Binary;

namespace GmailOrganiser.Analysis.Attachments;

/// <summary>
/// The pixel count an image declares in its header, read without decoding it, so a small file declaring a huge canvas
/// (a decompression bomb) never reaches Tesseract, Leptonica or Ollama, which allocate the whole canvas first. Every
/// format <see cref="ImageTextReader.MediaType"/> knows is read from the fields its decoder trusts.
/// </summary>
public static class ImageHeader
{
    /// <summary>Declared width × height above which an image or PDF page image is not decoded (an A4 page at 600 dpi is 35 MP).</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>
    /// More components (samples per pixel) than grey, RGB, RGBA or CMYK: decoders that allocate a buffer per component would
    /// multiply the capped canvas by this count.
    /// </summary>
    private const int MaxComponents = 4;

    /// <summary>More TIFF pages than any scan in a mail; Tesseract would read every one.</summary>
    private const int MaxTiffPages = 1000;

    /// <summary>
    /// Deeper animation frames nested in animation frames than any decoder reads (libwebp reads none): each level is a
    /// recursive call, so an unbounded chain could exhaust the stack.
    /// </summary>
    private const int MaxWebPFrameDepth = 8;

    /// <summary>Sniffs the media type first; <see cref="DeclaredPixels(string?, ReadOnlySpan{byte})"/> when the caller has it.</summary>
    public static long? DeclaredPixels(ReadOnlySpan<byte> image) => DeclaredPixels(ImageTextReader.MediaType(image), image);

    /// <summary>
    /// The declared pixels: the largest page (or tile) of a TIFF, every frame of a GIF together (giflib decodes them all),
    /// width × height otherwise. <c>null</c> when the format is unknown or the header is truncated, malformed or declares
    /// no pixels.
    /// </summary>
    /// <param name="mediaType"><see cref="ImageTextReader.MediaType"/> of <paramref name="image"/>.</param>
    public static long? DeclaredPixels(string? mediaType, ReadOnlySpan<byte> image)
    {
        var pixels = mediaType switch
        {
            "image/png" => Png(image),
            "image/jpeg" => Jpeg(image),
            "image/gif" => Gif(image),
            "image/webp" => WebP(image),
            "image/tiff" => Tiff(image),
            "image/bmp" => Bmp(image),
            "image/jp2" => Jp2(image),
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
    /// frame with more than <see cref="MaxComponents"/> components declares nothing (libjpeg allocates coefficient arrays
    /// per component before Leptonica rejects the image). Segments such as EXIF thumbnails are skipped by length; stray
    /// bytes, 0xFF fill bytes and entropy-coded data are skipped byte by byte, as libjpeg does when it looks for the next
    /// marker.
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
    /// Every frame added up, not the logical-screen canvas: giflib's <c>DGifSlurp</c> allocates each frame's raster and
    /// nothing for the screen, so a single-frame GIF counts once.
    /// </summary>
    private static long? Gif(ReadOnlySpan<byte> s)
    {
        if (s.Length < 13)
        {
            return null;
        }

        long pixels = 0;
        var i = 13 + ColorTableSize(s[10]);
        while (i < s.Length && s[i] != 0x3B)
        {
            if (s[i] == 0x21)
            {
                i = SkipSubBlocks(s, i + 2);
            }
            else if (s[i] == 0x2C && i + 10 <= s.Length)
            {
                pixels += (long)BinaryPrimitives.ReadUInt16LittleEndian(s[(i + 5)..]) * BinaryPrimitives.ReadUInt16LittleEndian(s[(i + 7)..]);
                i = SkipSubBlocks(s, i + 10 + ColorTableSize(s[i + 9]) + 1);
            }
            else
            {
                return null;
            }
        }

        return pixels;

        static int ColorTableSize(byte packed) => (packed & 0x80) == 0 ? 0 : 3 << ((packed & 7) + 1);

        static int SkipSubBlocks(ReadOnlySpan<byte> s, int i)
        {
            while (i < s.Length && s[i] != 0)
            {
                i += s[i] + 1;
            }

            return i + 1;
        }
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

    /// <summary>
    /// The largest page, or a page's tile when that is larger (libtiff sizes its tile buffer from TileWidth × TileLength,
    /// not from the image): Tesseract reads a multi-page TIFF one page at a time. A page with more than
    /// <see cref="MaxComponents"/> samples per pixel, or repeating a size, tile or samples tag, declares nothing.
    /// </summary>
    private static long? Tiff(ReadOnlySpan<byte> s)
    {
        var little = s[0] == 0x49;
        long largest = 0;
        var visited = new HashSet<uint>();
        var offset = U32(s, 4, little);
        while (offset != 0)
        {
            if (visited.Count == MaxTiffPages || !visited.Add(offset) || offset > s.Length - 2)
            {
                return null;
            }

            var start = (int)offset;
            var count = U16(s, start, little);
            var end = start + 2 + (12 * count);
            if (end + 4 > s.Length)
            {
                return null;
            }

            uint width = 0, height = 0, samples = 1, tileWidth = 0, tileHeight = 0;
            var seen = 0;
            for (var entry = start + 2; entry < end; entry += 12)
            {
                // SHORT or LONG, one value, stored inline.
                var value = U16(s, entry + 2, little) switch
                {
                    3 => U16(s, entry + 8, little),
                    4 => U32(s, entry + 8, little),
                    _ => 0u,
                };

                // libtiff uses the first of a repeated tag, a parser could use the last: an IFD repeating one declares nothing.
                switch (U16(s, entry, little))
                {
                    case 256 when First(1):
                        width = value;
                        break;
                    case 257 when First(2):
                        height = value;
                        break;
                    case 277 when First(4):
                        samples = value;
                        break;
                    case 322 when First(8):
                        tileWidth = value;
                        break;
                    case 323 when First(16):
                        tileHeight = value;
                        break;
                    case 256 or 257 or 277 or 322 or 323:
                        return null;
                }
            }

            if (width == 0 || height == 0 || samples is 0 or > MaxComponents)
            {
                return null;
            }

            largest = Math.Max(largest, Math.Max(Area(width, height), Area(tileWidth, tileHeight)));
            offset = U32(s, end, little);

            bool First(int bit)
            {
                var first = (seen & bit) == 0;
                seen |= bit;
                return first;
            }
        }

        return largest;

        static ushort U16(ReadOnlySpan<byte> s, int at, bool little) =>
            little ? BinaryPrimitives.ReadUInt16LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

        static uint U32(ReadOnlySpan<byte> s, int at, bool little) =>
            s.Length < at + 4 ? 0
            : little ? BinaryPrimitives.ReadUInt32LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
    }

    /// <summary>
    /// A negative height is a top-down bitmap; the 12-byte OS/2 header has 16-bit sizes, every other known header 32-bit
    /// ones. An unknown header size declares nothing, since a decoder could read its sizes either way.
    /// </summary>
    private static long? Bmp(ReadOnlySpan<byte> s)
    {
        if (s.Length < 26)
        {
            return null;
        }

        switch (BinaryPrimitives.ReadUInt32LittleEndian(s[14..]))
        {
            case 12:
                return (long)BinaryPrimitives.ReadUInt16LittleEndian(s[18..]) * BinaryPrimitives.ReadUInt16LittleEndian(s[20..]);
            case 16 or 40 or 52 or 56 or 64 or 108 or 124:
                break;
            default:
                return null;
        }

        long width = BinaryPrimitives.ReadInt32LittleEndian(s[18..]);
        long height = BinaryPrimitives.ReadInt32LittleEndian(s[22..]);
        return width > 0 ? width * Math.Abs(height) : null;
    }

    /// <summary>
    /// The largest of the codestream's SIZ markers, which OpenJPEG decodes from, and the <c>ihdr</c> boxes in <c>jp2h</c>,
    /// which could disagree: a raw codestream as PDFs embed it, or the <c>jp2c</c> box of a JP2 file. OpenJPEG allocates a
    /// full canvas per component, so more than <see cref="MaxComponents"/> components anywhere declares nothing.
    /// </summary>
    private static long? Jp2(ReadOnlySpan<byte> s)
    {
        long largest = 0;
        var i = 0;
        while (s[i..] is not [0xFF, 0x4F, ..])
        {
            if (i + 8 > s.Length)
            {
                return null;
            }

            var length = (long)BinaryPrimitives.ReadUInt32BigEndian(s[i..]);
            var header = 8;
            if (length == 1)
            {
                if (i + 16 > s.Length)
                {
                    return null;
                }

                length = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(s[(i + 8)..]), int.MaxValue);
                header = 16;
            }

            var box = s[(i + 4)..(i + 8)];
            if (box.SequenceEqual("ihdr"u8))
            {
                // Height, width, components.
                if (i + header + 10 > s.Length || BinaryPrimitives.ReadUInt16BigEndian(s[(i + header + 8)..]) is 0 or > MaxComponents)
                {
                    return null;
                }

                largest = Math.Max(largest, Area(BinaryPrimitives.ReadUInt32BigEndian(s[(i + header + 4)..]), BinaryPrimitives.ReadUInt32BigEndian(s[(i + header)..])));
            }

            if (box.SequenceEqual("jp2c"u8) || box.SequenceEqual("jp2h"u8))
            {
                i += header;
            }
            else if (length >= header && length <= s.Length - i)
            {
                i += (int)length;
            }
            else
            {
                return null;
            }
        }

        // SOC, then SIZ first; any further SIZ in the main header (up to the first tile, SOT) counts too.
        if (i + 4 > s.Length || s[(i + 2)..(i + 4)] is not [0xFF, 0x51])
        {
            return null;
        }

        for (i += 2; i + 4 <= s.Length && s[i] == 0xFF && s[i + 1] is not (0x90 or 0xD9); i += 2 + BinaryPrimitives.ReadUInt16BigEndian(s[(i + 2)..]))
        {
            if (s[i + 1] == 0x51)
            {
                if (Siz(s[i..]) is not { } pixels)
                {
                    return null;
                }

                largest = Math.Max(largest, pixels);
            }
        }

        return largest;

        // SIZ, Lsiz, Rsiz, then Xsiz, Ysiz, XOsiz, YOsiz, XTsiz, YTsiz, XTOsiz, YTOsiz, Csiz.
        static long? Siz(ReadOnlySpan<byte> s)
        {
            if (s.Length < 40 || BinaryPrimitives.ReadUInt16BigEndian(s[38..]) is 0 or > MaxComponents)
            {
                return null;
            }

            var x = BinaryPrimitives.ReadUInt32BigEndian(s[6..]);
            var y = BinaryPrimitives.ReadUInt32BigEndian(s[10..]);
            var xOffset = BinaryPrimitives.ReadUInt32BigEndian(s[14..]);
            var yOffset = BinaryPrimitives.ReadUInt32BigEndian(s[18..]);
            return x > xOffset && y > yOffset ? Area(x - xOffset, y - yOffset) : null;
        }
    }

    /// <summary>Two 32-bit sides can't overflow 64 bits unsigned; saturates instead of turning negative.</summary>
    private static long Area(uint width, uint height) => (long)Math.Min((ulong)width * height, long.MaxValue);
}
