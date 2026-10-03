using System.Buffers.Binary;
using System.Text;
using GmailOrganiser.Analysis.Attachments;
using GmailOrganiser.Tests.Fakes;

namespace GmailOrganiser.Tests.Unit.Analysis;

/// <summary>
/// <see cref="ImageHeader"/> on tiny synthetic PNG, JPEG and WebP files that only declare their size (no pixel data), so
/// a decompression bomb is caught without building one. <see cref="ImageAttachmentTests"/> checks that OCR and vision
/// never see them, and that the other formats are left to the OCR memory limit.
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
        { "png-max", Png(uint.MaxValue, uint.MaxValue), long.MaxValue },
    };

    [Theory]
    [MemberData(nameof(Declared))]
    public void Reads_the_declared_size_of_png_jpeg_and_webp_without_decoding(string format, byte[] image, long pixels)
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
    public void Reads_the_vision_formats_only_and_leaves_the_rest_to_the_ocr_memory_limit()
    {
        foreach (var mediaType in (string?[])["image/png", "image/jpeg", "image/webp"])
        {
            ImageHeader.Reads(mediaType).ShouldBeTrue(mediaType);
            ImageTextReader.IsVisionFormat(mediaType).ShouldBeTrue(mediaType);
        }

        foreach (var mediaType in (string?[])["image/gif", "image/tiff", "image/bmp", "image/jp2", null])
        {
            ImageHeader.Reads(mediaType).ShouldBeFalse(mediaType);
        }

        // A GIF frame declaring 65535² and a 2-byte TIFF prefix: neither is parsed, neither throws.
        ImageHeader.DeclaredPixels([.. "GIF89a"u8, .. Le16(10), .. Le16(10), 0, 0, 0, 0x2C, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 2, 0, 0x3B]).ShouldBeNull();
        ImageHeader.DeclaredPixels("image/tiff", [0x49, 0x49]).ShouldBeNull();
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
    public void Jpeg_with_up_to_four_components_is_read() => ImageHeader.DeclaredPixels(Jpeg(100, 100, components: 4)).ShouldBe(10_000);

    /// <summary>Two headers that could disagree: the larger counts, whichever comes first, since decoders differ on which they use.</summary>
    public static TheoryData<string, byte[], long> Ambiguous => new()
    {
        { "jpeg-big-sof-first", JpegTwoFrames(60_000, 10), 3_600_000_000 },
        { "jpeg-big-sof-second", JpegTwoFrames(10, 60_000), 3_600_000_000 },
        { "webp-small-canvas-big-vp8", [.. WebPExtended(10, 10), .. WebPLossy(16_000, 16_000)[12..]], 256_000_000 },
        { "webp-big-canvas-small-vp8", [.. WebPExtended(16_000, 16_000), .. WebPLossy(10, 10)[12..]], 256_000_000 },
        { "webp-animation-frame", [.. WebPExtended(10, 10), .. WebPFrame(20_000, 20_000, WebPLossy(10, 10)[12..])], 400_000_000 },
        { "webp-animation-nested-vp8", [.. WebPExtended(10, 10), .. WebPFrame(10, 10, WebPLossy(16_000, 16_000)[12..])], 256_000_000 },
        { "webp-frames-nested-to-the-limit", [.. WebPExtended(10, 10), .. WebPNestedFrames(8, WebPLossy(16_000, 16_000)[12..])], 256_000_000 },
    };

    [Theory]
    [MemberData(nameof(Ambiguous))]
    public void Repeated_or_conflicting_headers_count_the_largest(string what, byte[] image, long pixels) =>
        ImageHeader.DeclaredPixels(image).ShouldBe(pixels, what);

    public static TheoryData<string, byte[]> Unreadable => new()
    {
        { "unknown", [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29] },
        { "png-truncated", Png(10, 10)[..20] },
        { "png-zero-width", Png(0, 10) },
        { "png-second-ihdr", [.. Png(10, 10), .. Png(30_000, 30_000)[8..]] },
        { "jpeg-scan-before-frame", [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6] },
        { "jpeg-scan-before-frame-after-fill", [0xFF, 0xD8, 0xFF, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xC0, 0, 11, 8, 0, 10, 0, 10, 1, 1, 0x11, 0] },
        { "jpeg-no-frame", [0xFF, 0xD8, 0xFF, 0xD9] },
        { "jpeg-10-components", Jpeg(6_000, 6_000, components: 10) },
        { "jpeg-no-components", Jpeg(10, 10, components: 0) },
        { "webp-unknown-chunk", [.. WebPExtended(10, 10)[..12], .. "ABCD"u8, .. new byte[14]] },
        { "webp-frames-nested-past-the-limit", [.. WebPExtended(10, 10), .. WebPNestedFrames(9, WebPLossy(10, 10)[12..])] },
        { "webp-frames-nested-100-000-deep", [.. WebPExtended(10, 10), .. WebPNestedFrames(100_000, WebPLossy(10, 10)[12..])] },
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void Unknown_truncated_or_malformed_headers_declare_nothing(string what, byte[] image) =>
        ImageHeader.DeclaredPixels(image).ShouldBeNull(what);

    public static byte[] Png(uint width, uint height) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. "IHDR"u8, .. Be32(width), .. Be32(height), 8, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>SOI, a JFIF APP0 segment, then a baseline frame header with <paramref name="components"/> components.</summary>
    public static byte[] Jpeg(ushort width, ushort height, byte components = 1) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0,
         0xFF, 0xC0, .. Be16((ushort)(8 + (3 * components))), 8, .. Be16(height), .. Be16(width), components,
         .. Enumerable.Range(1, components).SelectMany(id => (byte[])[(byte)id, 0x11, 0]), 0xFF, 0xD9];

    public static byte[] WebPLossy(ushort width, ushort height) =>
        WebP("VP8 ", [0, 0, 0, 0x9D, 0x01, 0x2A, .. Le16(width), .. Le16(height)]);

    public static byte[] WebPLossless(uint width, uint height) =>
        WebP("VP8L", [0x2F, .. Le32((width - 1) | ((height - 1) << 14)), 0, 0, 0, 0, 0]);

    public static byte[] WebPExtended(uint width, uint height) =>
        WebP("VP8X", [0, 0, 0, 0, .. Le32(width - 1)[..3], .. Le32(height - 1)[..3]]);

    /// <summary>SOI, then a small and a large baseline frame header in the given order, then EOI.</summary>
    private static byte[] JpegTwoFrames(ushort first, ushort second) =>
        [0xFF, 0xD8, 0xFF, 0xC0, 0, 11, 8, .. Be16(first), .. Be16(first), 1, 1, 0x11, 0,
         0xFF, 0xC0, 0, 11, 8, .. Be16(second), .. Be16(second), 1, 1, 0x11, 0, 0xFF, 0xD9];

    /// <summary>An ANMF chunk: offset, size, duration and flags, then the frame's own chunks.</summary>
    private static byte[] WebPFrame(uint width, uint height, byte[] frame) =>
        [.. "ANMF"u8, .. Le32((uint)frame.Length + 16), 0, 0, 0, 0, 0, 0, .. Le32(width - 1)[..3], .. Le32(height - 1)[..3], 0, 0, 0, 0, .. frame];

    /// <summary>
    /// <paramref name="depth"/> 10 × 10 ANMF chunks, each holding the next, the innermost holding <paramref name="frame"/>;
    /// written outermost first so a chain of any length costs one pass.
    /// </summary>
    private static byte[] WebPNestedFrames(int depth, byte[] frame)
    {
        var bytes = new List<byte>((depth * 24) + frame.Length);
        for (var level = 0; level < depth; level++)
        {
            bytes.AddRange("ANMF"u8);
            bytes.AddRange(Le32((uint)(16 + ((depth - 1 - level) * 24) + frame.Length)));
            bytes.AddRange([0, 0, 0, 0, 0, 0, 9, 0, 0, 9, 0, 0, 0, 0, 0, 0]);
        }

        bytes.AddRange(frame);
        return [.. bytes];
    }

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
