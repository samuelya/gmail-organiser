using System.Buffers.Binary;
using System.Text;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Tests.Fakes;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>
/// <see cref="ImageHeader"/> on tiny synthetic files that only declare their size (no pixel data), so a decompression
/// bomb is caught without building one. <see cref="ImageAttachmentTests"/> checks that OCR and vision never see them.
/// </summary>
public sealed class ImageHeaderTests
{
    public static TheoryData<string, byte[], long> Declared => new()
    {
        { "png", Png(30_000, 30_000), 900_000_000 },
        { "jpeg", Jpeg(65_535, 65_535), 65_535L * 65_535 },
        { "webp-vp8", WebPLossy(16_383, 16_383), 16_383L * 16_383 },
        { "webp-vp8l", WebPLossless(16_384, 16_384), 16_384L * 16_384 },
        { "webp-vp8x", WebPExtended(100_000, 100_000), 10_000_000_000 },
        { "gif", Gif(65_535, 65_535), 65_535L * 65_535 * 2 },
        { "tiff", Tiff(100_000, 100_000), 10_000_000_000 },
        { "bmp-top-down", Bmp(20_000, -20_000), 400_000_000 },
        { "jp2-codestream", Jp2(20_000, 20_000, boxed: false), 400_000_000 },
        { "jp2-file", Jp2(20_000, 20_000, boxed: true), 400_000_000 },
        { "png-max", Png(uint.MaxValue, uint.MaxValue), long.MaxValue },
    };

    [Theory]
    [MemberData(nameof(Declared))]
    public void Reads_the_declared_size_of_every_known_format_without_decoding(string format, byte[] image, long pixels)
    {
        ImageHeader.DeclaredPixels(image).ShouldBe(pixels, format);
        image.Length.ShouldBeLessThan(100);
    }

    [Fact]
    public void Real_synthetic_images_are_well_under_the_cap()
    {
        ImageHeader.DeclaredPixels(SyntheticImage.Png).ShouldBe(420 * 130);
        ImageHeader.DeclaredPixels(SyntheticImage.Jpeg).ShouldBe(420 * 130);
    }

    [Fact]
    public void Gif_counts_the_canvas_and_every_frame_because_giflib_decodes_them_all()
    {
        // 16 MP canvas and two 16 MP frames: each under the cap, together over it.
        ImageHeader.DeclaredPixels(Gif(4_000, 4_000, frames: 2)).ShouldBe(48_000_000);
    }

    [Fact]
    public void Jpeg_skips_stray_and_fill_bytes_before_a_marker_as_libjpeg_does()
    {
        var jpeg = Jpeg(420, 130);
        var frame = Array.IndexOf(jpeg, (byte)0xC0) - 1;
        byte[] padded = [.. jpeg[..frame], 0x00, 0x12, 0x34, 0xFF, 0xFF, 0xFF, .. jpeg[frame..]];

        ImageHeader.DeclaredPixels(padded).ShouldBe(420 * 130);
    }

    [Fact]
    public void Up_to_four_components_are_read()
    {
        ImageHeader.DeclaredPixels(Jp2(100, 100, boxed: true, components: 4)).ShouldBe(10_000);
        ImageHeader.DeclaredPixels(Tiff(100, 100, samples: 4)).ShouldBe(10_000);
    }

    public static TheoryData<string, byte[]> Unreadable => new()
    {
        { "unknown", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29] },
        { "png-truncated", Png(10, 10)[..20] },
        { "png-zero-width", Png(0, 10) },
        { "jpeg-scan-before-frame", [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6] },
        { "jpeg-no-frame", [0xFF, 0xD8, 0xFF, 0xD9] },
        { "webp-unknown-chunk", [.. WebPExtended(10, 10)[..12], .. "ABCD"u8, .. new byte[14]] },
        { "tiff-ifd-loop", TiffLoop() },
        { "tiff-no-ifd", [0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0] },
        { "bmp-truncated", Bmp(10, 10)[..20] },
        { "jp2-1000-components", Jp2(6_000, 6_000, boxed: false, components: 1_000) },
        { "jp2-no-components", Jp2(10, 10, boxed: true, components: 0) },
        { "tiff-5-samples", Tiff(6_000, 6_000, samples: 5) },
        { "jp2-without-codestream", [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A, 0, 0, 0, 0] },
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void Unknown_truncated_or_malformed_headers_declare_nothing(string what, byte[] image) =>
        ImageHeader.DeclaredPixels(image).ShouldBeNull(what);

    public static byte[] Png(uint width, uint height) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, .. Be32(width), .. Be32(height), 8, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>SOI, a JFIF APP0 segment, then a baseline frame header.</summary>
    public static byte[] Jpeg(ushort width, ushort height) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0,
         0xFF, 0xC0, 0, 11, 8, .. Be16(height), .. Be16(width), 1, 1, 0x11, 0, 0xFF, 0xD9];

    public static byte[] WebPLossy(ushort width, ushort height) =>
        WebP("VP8 ", [0, 0, 0, 0x9D, 0x01, 0x2A, .. Le16(width), .. Le16(height)]);

    public static byte[] WebPLossless(uint width, uint height) =>
        WebP("VP8L", [0x2F, .. Le32((width - 1) | ((height - 1) << 14)), 0, 0, 0, 0, 0]);

    public static byte[] WebPExtended(uint width, uint height) =>
        WebP("VP8X", [0, 0, 0, 0, .. Le32(width - 1)[..3], .. Le32(height - 1)[..3]]);

    public static byte[] Gif(ushort width, ushort height, int frames = 1)
    {
        byte[] frame = [0x2C, 0, 0, 0, 0, .. Le16(width), .. Le16(height), 0, 2, 0];
        return [.. "GIF89a"u8, .. Le16(width), .. Le16(height), 0, 0, 0, .. Enumerable.Repeat(frame, frames).SelectMany(f => f), 0x3B];
    }

    public static byte[] Tiff(uint width, uint height, uint samples = 3) =>
        [0x49, 0x49, 0x2A, 0x00, .. Le32(8), 0x03, 0x00, .. TiffEntry(256, width), .. TiffEntry(257, height), .. TiffEntry(277, samples), .. Le32(0)];

    public static byte[] Bmp(int width, int height) =>
        [.. "BM"u8, .. new byte[12], .. Le32(40), .. Le32((uint)width), .. Le32((uint)height), .. new byte[28]];

    /// <summary>A raw codestream (SOC, SIZ) as PDFs embed it, or wrapped in a JP2 signature box and a <c>jp2c</c> box.</summary>
    public static byte[] Jp2(uint width, uint height, bool boxed, ushort components = 3)
    {
        byte[] codestream = [0xFF, 0x4F, 0xFF, 0x51, 0, 41, 0, 0, .. Be32(width), .. Be32(height), .. new byte[24], .. Be16(components)];
        return boxed
            ? [0, 0, 0, 12, .. "jP  "u8, 0x0D, 0x0A, 0x87, 0x0A, .. Be32((uint)codestream.Length + 8), .. "jp2c"u8, .. codestream]
            : codestream;
    }

    private static byte[] TiffLoop() =>
        [0x49, 0x49, 0x2A, 0x00, .. Le32(8), 0x02, 0x00, .. TiffEntry(256, 10), .. TiffEntry(257, 10), .. Le32(8)];

    private static byte[] TiffEntry(ushort tag, uint value) => [.. Le16(tag), 4, 0, 1, 0, 0, 0, .. Le32(value)];

    private static byte[] WebP(string chunk, byte[] payload) =>
        [.. "RIFF"u8, .. Le32((uint)payload.Length + 12), .. "WEBP"u8, .. Encoding.ASCII.GetBytes(chunk), .. Le32((uint)payload.Length), .. payload];

    private static byte[] Be16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static byte[] Be32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Le16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] Le32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }
}
