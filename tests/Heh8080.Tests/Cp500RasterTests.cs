using Heh8080.Devices;

namespace Heh8080.Tests;

/// <summary>Rasterizing CP-500 video state against a synthetic character generator in which every glyph is distinct.</summary>
public sealed class Cp500RasterTests
{
    private const int Width = Cp500Raster.Width;
    private const byte Space = 0x20;

    private static readonly byte[] SyntheticImage = BuildSyntheticImage();
    private static readonly Cp500CharacterGenerator Generator = new(SyntheticImage);

    private static byte[] BuildSyntheticImage()
    {
        byte[] image = new byte[Cp500CharacterGenerator.ImageSize];
        uint state = 12345;
        for (int i = 0; i < image.Length; i++)
        {
            state = state * 1664525 + 1013904223;
            image[i] = (byte)((state >> 24) | 0x81);
        }

        Array.Clear(image, Space * Cp500CharacterGenerator.GlyphHeight, Cp500CharacterGenerator.GlyphHeight);
        return image;
    }

    private static byte GlyphRow(int code, int row) => SyntheticImage[code * 8 + row];

    private static byte[] Render(byte control, params (int Column, int Row, byte Code)[] cells)
    {
        byte[] videoRam = new byte[Cp500VideoDevice.CharacterCount];
        Array.Fill(videoRam, Space);
        foreach (var (column, row, code) in cells)
            videoRam[row * Cp500VideoDevice.Columns + column] = code;

        byte[] frame = new byte[Cp500Raster.Width * Cp500Raster.Height];
        Cp500Raster.Render(Generator, videoRam, control, frame);
        return frame;
    }

    /// <summary>The 8 pixels of one scanline starting at an x position, bit 7 first.</summary>
    private static byte Bits(byte[] frame, int x, int y)
    {
        int bits = 0;
        for (int i = 0; i < 8; i++)
            bits = (bits << 1) | frame[y * Width + x + i];

        return (byte)bits;
    }

    private static void AssertCell(byte[] frame, int column, int row, Func<int, byte> expectedScanline)
    {
        for (int scan = 0; scan < Cp500Raster.CellHeight; scan++)
        {
            Assert.True(
                expectedScanline(scan) == Bits(frame, column * 8, row * 12 + scan),
                $"cell ({column},{row}) scanline {scan}");
        }
    }

    [Fact]
    public void TheSyntheticGeneratorGivesEveryCodeItsOwnGlyph()
    {
        var glyphs = Enumerable.Range(0, 256)
            .Select(code => Convert.ToHexString(SyntheticImage.AsSpan(code * 8, 8)))
            .Distinct();

        Assert.Equal(256, glyphs.Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2047)]
    [InlineData(2049)]
    public void AGeneratorImageMustBeExactlyTwoKiB(int length)
    {
        Assert.Throws<ArgumentException>(() => new Cp500CharacterGenerator(new byte[length]));
    }

    [Fact]
    public void TheFrameIsFiveHundredTwelveByOneHundredNinetyTwoPixels()
    {
        Assert.Equal(512, Cp500Raster.Width);
        Assert.Equal(192, Cp500Raster.Height);
        Assert.Equal(Cp500VideoDevice.Columns * Cp500Raster.CellWidth, Cp500Raster.Width);
        Assert.Equal(Cp500VideoDevice.Rows * Cp500Raster.CellHeight, Cp500Raster.Height);
    }

    [Fact]
    public void FramesAndVideoMemoryOfTheWrongSizeAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => Cp500Raster.Render(Generator, new byte[Cp500VideoDevice.CharacterCount], 0, new byte[10]));
        Assert.Throws<ArgumentException>(
            () => Cp500Raster.Render(Generator, new byte[10], 0, new byte[Cp500Raster.Width * Cp500Raster.Height]));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x1F)]
    [InlineData(0x41)]
    [InlineData(0x7F)]
    public void CodesBelow80ShowTheGeneratorGlyphWithTheSameCode(byte code)
    {
        byte[] frame = Render(0, (3, 2, code));

        AssertCell(frame, 3, 2, scan => scan < 8 ? GlyphRow(code, scan) : (byte)0);
    }

    [Fact]
    public void GlyphsSitInTheTopEightScanlinesOfTheTwelveScanlineCell()
    {
        byte[] frame = Render(0, (0, 0, 0x41), (0, 1, 0x41));

        for (int scan = 0; scan < 8; scan++)
            Assert.Equal(GlyphRow(0x41, scan), Bits(frame, 0, scan));
        for (int scan = 8; scan < 12; scan++)
            Assert.Equal(0, Bits(frame, 0, scan));
        Assert.Equal(GlyphRow(0x41, 0), Bits(frame, 0, 12));
    }

    [Fact]
    public void EachCellOccupiesItsOwnEightByTwelveRectangle()
    {
        byte[] frame = Render(0, (63, 15, 0x41));

        AssertCell(frame, 63, 15, scan => scan < 8 ? GlyphRow(0x41, scan) : (byte)0);
        Assert.Equal(0, frame.Take(Width * 15 * 12).Sum(pixel => pixel));
        Assert.Equal(0, Bits(frame, 62 * 8, 15 * 12));
    }

    [Theory]
    [InlineData(0x80, 0x00)]
    [InlineData(0x81, 0x01)]
    [InlineData(0xA5, 0x25)]
    [InlineData(0xBF, 0x3F)]
    public void CodesFrom80ToBFAreTwoByThreeBlocksWhateverTheCharacterSet(byte code, byte blocks)
    {
        foreach (byte control in new byte[] { 0x00, 0x08 })
        {
            byte[] frame = Render(control, (0, 0, code));

            AssertCell(frame, 0, 0, scan => ExpectedBlockScanline(blocks, scan));
        }
    }

    private static byte ExpectedBlockScanline(byte blocks, int scan)
    {
        int band = scan / 4;
        bool left = (blocks & (1 << (band * 2))) != 0;
        bool right = (blocks & (1 << (band * 2 + 1))) != 0;
        return (byte)((left ? 0xF0 : 0) | (right ? 0x0F : 0));
    }

    [Fact]
    public void BlockBitsRunTopLeftTopRightMiddleLeftMiddleRightBottomLeftBottomRight()
    {
        (int Bit, int X, int Y)[] expected =
        [
            (0, 0, 0), (1, 4, 0), (2, 0, 4), (3, 4, 4), (4, 0, 8), (5, 4, 8)
        ];

        foreach (var (bit, x, y) in expected)
        {
            byte[] frame = Render(0, (0, 0, (byte)(0x80 | (1 << bit))));

            Assert.Equal(16, frame.Sum(pixel => pixel));
            Assert.Equal(1, frame[y * Width + x]);
            Assert.Equal(1, frame[(y + 3) * Width + x + 3]);
        }
    }

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC1)]
    [InlineData(0xFF)]
    public void CodesFromC0ToFFShowTheSpecialSetWhichIsGlyph80ToBF(byte code)
    {
        byte[] frame = Render(0x00, (0, 0, code));

        AssertCell(frame, 0, 0, scan => scan < 8 ? GlyphRow(code & 0xBF, scan) : (byte)0);
    }

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC1)]
    [InlineData(0xFF)]
    public void CodesFromC0ToFFShowTheAlternateSetWhenEcBit3IsSet(byte code)
    {
        byte[] frame = Render(0x08, (0, 0, code));

        AssertCell(frame, 0, 0, scan => scan < 8 ? GlyphRow(code, scan) : (byte)0);
    }

    [Fact]
    public void CodesBelow80IgnoreTheAlternateSet()
    {
        Assert.Equal(Render(0x00, (0, 0, 0x41)), Render(0x08, (0, 0, 0x41)));
    }

    [Fact]
    public void ThirtyTwoColumnModeShowsEvenVramColumnsWithEveryPixelDoubled()
    {
        byte[] frame = Render(0x04, (0, 0, 0x41), (1, 0, 0x42), (2, 0, 0x43), (62, 0, 0x44), (63, 0, 0x45));

        AssertWideCell(frame, visibleColumn: 0, code: 0x41);
        AssertWideCell(frame, visibleColumn: 1, code: 0x43);
        AssertWideCell(frame, visibleColumn: 31, code: 0x44);
    }

    private static void AssertWideCell(byte[] frame, int visibleColumn, byte code)
    {
        for (int scan = 0; scan < 12; scan++)
        {
            byte source = scan < 8 ? GlyphRow(code, scan) : (byte)0;
            for (int pixel = 0; pixel < 16; pixel++)
            {
                byte expected = (byte)((source >> (7 - pixel / 2)) & 1);
                Assert.True(
                    expected == frame[scan * Width + visibleColumn * 16 + pixel],
                    $"visible column {visibleColumn} scanline {scan} pixel {pixel}");
            }
        }
    }

    [Fact]
    public void ThirtyTwoColumnModeDoublesBlocksToEightPixelsWide()
    {
        byte[] frame = Render(0x04, (0, 0, 0x81), (2, 0, 0xBF));

        Assert.Equal(1, frame[0]);
        Assert.Equal(1, frame[3 * Width + 7]);
        Assert.Equal(0, frame[8]);
        Assert.Equal(0, frame[4 * Width]);
        Assert.Equal(1, frame[16 + 8 * Width + 15]);
    }

    [Fact]
    public void Bit7OfTheControlLatchIsNotAVideoFeature()
    {
        Assert.Equal(Render(0x00, (0, 0, 0x41)), Render(0x80, (0, 0, 0x41)));
    }

    [RealCharacterGeneratorFact]
    public void TheRealBootBannerIsDrawnWithTheGlyphsOfTheCharacterRom()
    {
        const string banner = "PROLOGICA CP-500";
        byte[] image = File.ReadAllBytes(Cp500RomFile.CharacterGeneratorPath!);
        var session = new RealRomSession();
        session.Machine.ResetHoldingBreak();
        Assert.True(session.RunUntilScreenContains(banner, maxSeconds: 10));
        string[] lines = session.Screen().Split('\n');
        int row = Array.FindIndex(lines, line => line.Contains(banner));
        int column = lines[row].IndexOf(banner, StringComparison.Ordinal);
        byte[] frame = new byte[Cp500Raster.Width * Cp500Raster.Height];

        session.Machine.Video.Render(new Cp500CharacterGenerator(image), frame);

        for (int i = 0; i < banner.Length; i++)
        {
            int glyph = banner[i];
            for (int scan = 0; scan < Cp500Raster.CellHeight; scan++)
            {
                byte expected = scan < 8 ? image[glyph * 8 + scan] : (byte)0;
                Assert.True(
                    expected == Bits(frame, (column + i) * 8, row * 12 + scan),
                    $"'{banner[i]}' scanline {scan}");
            }
        }

        Assert.Equal(0, Bits(frame, (column - 1) * 8, row * 12 + 3));
        Assert.NotEqual(0, Enumerable.Range(0, 8).Sum(scan => Bits(frame, column * 8, row * 12 + scan)));
    }

    [Fact]
    public void TheVideoDeviceRendersItsOwnVramAndControl()
    {
        var video = new Cp500VideoDevice();
        video.VideoRam.Write(Cp500VideoDevice.StartAddress + 2, 0x41);
        video.Out(Cp500VideoDevice.ControlPort, 0x04);
        byte[] frame = new byte[Cp500Raster.Width * Cp500Raster.Height];

        video.Render(Generator, frame);

        AssertWideCell(frame, visibleColumn: 1, code: 0x41);
    }
}
