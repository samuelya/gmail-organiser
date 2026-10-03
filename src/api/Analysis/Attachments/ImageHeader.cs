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

    /// <summary>More TIFF pages than any scan in a mail; Tesseract would read every one.</summary>
    private const int MaxTiffPages = 1000;

    /// <summary>
    /// The declared pixels: the largest page of a TIFF, the canvas plus every frame of a GIF (giflib decodes them all),
    /// width × height otherwise. <c>null</c> when the format is unknown or the header is truncated, malformed or declares
    /// no pixels.
    /// </summary>
    public static long? DeclaredPixels(ReadOnlySpan<byte> image)
    {
        var pixels = ImageTextReader.MediaType(image) switch
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

    private static long? Png(ReadOnlySpan<byte> s) =>
        s.Length >= 24 && s[12..16].SequenceEqual("IHDR"u8)
            ? Area(BinaryPrimitives.ReadUInt32BigEndian(s[16..]), BinaryPrimitives.ReadUInt32BigEndian(s[20..]))
            : null;

    /// <summary>The first frame header (SOF0–SOF15) before the scan starts; segments such as EXIF thumbnails are skipped.</summary>
    private static long? Jpeg(ReadOnlySpan<byte> s)
    {
        var i = 2;
        while (i + 1 < s.Length)
        {
            if (s[i] != 0xFF)
            {
                return null;
            }

            var marker = s[i + 1];
            i += marker == 0xFF ? 1 : 2;
            if (marker is 0xFF or 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                continue;
            }

            if (marker is 0xD9 or 0xDA || i + 2 > s.Length)
            {
                return null;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(s[i..]);
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return i + 7 <= s.Length
                    ? (long)BinaryPrimitives.ReadUInt16BigEndian(s[(i + 3)..]) * BinaryPrimitives.ReadUInt16BigEndian(s[(i + 5)..])
                    : null;
            }

            if (length < 2)
            {
                return null;
            }

            i += length;
        }

        return null;
    }

    private static long? Gif(ReadOnlySpan<byte> s)
    {
        if (s.Length < 13)
        {
            return null;
        }

        long pixels = (long)BinaryPrimitives.ReadUInt16LittleEndian(s[6..]) * BinaryPrimitives.ReadUInt16LittleEndian(s[8..]);
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

    private static long? WebP(ReadOnlySpan<byte> s)
    {
        if (s.Length < 30)
        {
            return null;
        }

        var chunk = s[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
        {
            return s[23..] is [0x9D, 0x01, 0x2A, ..]
                ? (long)(BinaryPrimitives.ReadUInt16LittleEndian(s[26..]) & 0x3FFF) * (BinaryPrimitives.ReadUInt16LittleEndian(s[28..]) & 0x3FFF)
                : null;
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(s[21..]);
            return s[20] == 0x2F ? (long)((bits & 0x3FFF) + 1) * (((bits >> 14) & 0x3FFF) + 1) : null;
        }

        // VP8X: the canvas; libwebp rejects a still image whose frame doesn't match it, and no frame of an animation exceeds it.
        return chunk.SequenceEqual("VP8X"u8) ? (long)(UInt24(s[24..]) + 1) * (UInt24(s[27..]) + 1) : null;

        static int UInt24(ReadOnlySpan<byte> b) => b[0] | b[1] << 8 | b[2] << 16;
    }

    /// <summary>The largest page: Tesseract reads a multi-page TIFF one page at a time.</summary>
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

            uint width = 0, height = 0;
            for (var entry = start + 2; entry < end; entry += 12)
            {
                var value = U16(s, entry + 2, little) switch
                {
                    3 => U16(s, entry + 8, little),
                    4 => U32(s, entry + 8, little),
                    _ => 0u,
                };
                (width, height) = U16(s, entry, little) switch
                {
                    256 => (value, height),
                    257 => (width, value),
                    _ => (width, height),
                };
            }

            if (width == 0 || height == 0)
            {
                return null;
            }

            largest = Math.Max(largest, Area(width, height));
            offset = U32(s, end, little);
        }

        return largest;

        static ushort U16(ReadOnlySpan<byte> s, int at, bool little) =>
            little ? BinaryPrimitives.ReadUInt16LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

        static uint U32(ReadOnlySpan<byte> s, int at, bool little) =>
            s.Length < at + 4 ? 0
            : little ? BinaryPrimitives.ReadUInt32LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);
    }

    /// <summary>A negative height is a top-down bitmap; the 12-byte OS/2 header has 16-bit sizes.</summary>
    private static long? Bmp(ReadOnlySpan<byte> s)
    {
        if (s.Length < 26)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(s[14..]) == 12)
        {
            return (long)BinaryPrimitives.ReadUInt16LittleEndian(s[18..]) * BinaryPrimitives.ReadUInt16LittleEndian(s[20..]);
        }

        long width = BinaryPrimitives.ReadInt32LittleEndian(s[18..]);
        long height = BinaryPrimitives.ReadInt32LittleEndian(s[22..]);
        return width > 0 ? width * Math.Abs(height) : null;
    }

    /// <summary>
    /// The codestream's SIZ marker, which OpenJPEG decodes from (not the <c>jp2h</c> box, which could disagree): a raw
    /// codestream as PDFs embed it, or the <c>jp2c</c> box of a JP2 file.
    /// </summary>
    private static long? Jp2(ReadOnlySpan<byte> s)
    {
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

            if (s[(i + 4)..(i + 8)].SequenceEqual("jp2c"u8))
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

        // SOC, SIZ, Lsiz, Rsiz, then Xsiz, Ysiz, XOsiz, YOsiz.
        if (i + 24 > s.Length || s[(i + 2)..(i + 4)] is not [0xFF, 0x51])
        {
            return null;
        }

        var x = BinaryPrimitives.ReadUInt32BigEndian(s[(i + 8)..]);
        var y = BinaryPrimitives.ReadUInt32BigEndian(s[(i + 12)..]);
        var xOffset = BinaryPrimitives.ReadUInt32BigEndian(s[(i + 16)..]);
        var yOffset = BinaryPrimitives.ReadUInt32BigEndian(s[(i + 20)..]);
        return x > xOffset && y > yOffset ? Area(x - xOffset, y - yOffset) : null;
    }

    /// <summary>Two 32-bit sides can't overflow 64 bits unsigned; saturates instead of turning negative.</summary>
    private static long Area(uint width, uint height) => (long)Math.Min((ulong)width * height, long.MaxValue);
}
