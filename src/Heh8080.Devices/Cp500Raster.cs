namespace Heh8080.Devices;

/// <summary>
/// Turns CP-500 video state into the native Model III raster: 64 by 16 cells of 8 by 12 pixels, 512 by 192 in all.
/// A frame holds one byte per pixel, 1 lit and 0 dark, row by row. Hosts scale it; this type knows no UI.
/// </summary>
public static class Cp500Raster
{
    /// <summary>Pixels per cell horizontally.</summary>
    public const int CellWidth = 8;

    /// <summary>Scanlines per cell. The glyph fills the top eight; the rest is blank, where a block cursor sits.</summary>
    public const int CellHeight = 12;

    /// <summary>Frame width in pixels.</summary>
    public const int Width = Cp500VideoDevice.Columns * CellWidth;

    /// <summary>Frame height in scanlines.</summary>
    public const int Height = Cp500VideoDevice.Rows * CellHeight;

    private const int BlockSize = 4;
    private const byte LeftBlock = 0xF0;
    private const byte RightBlock = 0x0F;

    /// <summary>
    /// Draws a snapshot of video RAM and the ECh control value. Codes 00h-7Fh show the glyph of the same code, 80h-BFh
    /// are 2-by-3 block graphics, C0h-FFh show glyph 80h-BFh (special set), or C0h-FFh itself (alternate set, ECh bit 3).
    /// In 32-column mode (ECh bit 2) only even video columns show and every pixel is doubled horizontally.
    /// </summary>
    public static void Render(
        Cp500CharacterGenerator generator, ReadOnlySpan<byte> videoRam, byte control, Span<byte> frame)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (videoRam.Length != Cp500VideoDevice.CharacterCount)
            throw new ArgumentException("Video RAM must hold every character cell.", nameof(videoRam));
        if (frame.Length != Width * Height)
            throw new ArgumentException($"A frame is {Width} by {Height} pixels.", nameof(frame));

        bool alternate = (control & Cp500VideoDevice.AlternateCharacterSetBit) != 0;
        int step = (control & Cp500VideoDevice.ThirtyTwoColumnBit) != 0 ? 2 : 1;

        for (int row = 0; row < Cp500VideoDevice.Rows; row++)
        {
            for (int scanline = 0; scanline < CellHeight; scanline++)
            {
                Span<byte> line = frame.Slice((row * CellHeight + scanline) * Width, Width);
                int x = 0;
                for (int column = 0; column < Cp500VideoDevice.Columns; column += step)
                {
                    byte code = videoRam[row * Cp500VideoDevice.Columns + column];
                    x = Plot(line, x, Scanline(generator, code, scanline, alternate), step);
                }
            }
        }
    }

    private static byte Scanline(Cp500CharacterGenerator generator, byte code, int scanline, bool alternate) => code switch
    {
        >= 0x80 and < 0xC0 => BlockScanline(code, scanline),
        >= 0xC0 when !alternate => GlyphScanline(generator, code & 0xBF, scanline),
        _ => GlyphScanline(generator, code, scanline)
    };

    private static byte GlyphScanline(Cp500CharacterGenerator generator, int glyph, int scanline) =>
        scanline < Cp500CharacterGenerator.GlyphHeight ? generator.GetScanline(glyph, scanline) : (byte)0;

    /// <summary>Bits 0-5 of the code light top left, top right, middle left, middle right, bottom left, bottom right.</summary>
    private static byte BlockScanline(byte code, int scanline)
    {
        int pair = (code >> (scanline / BlockSize * 2)) & 3;
        return (byte)(((pair & 1) != 0 ? LeftBlock : 0) | ((pair & 2) != 0 ? RightBlock : 0));
    }

    private static int Plot(Span<byte> line, int x, byte bits, int repeat)
    {
        for (int bit = 7; bit >= 0; bit--)
        {
            byte pixel = (byte)((bits >> bit) & 1);
            for (int i = 0; i < repeat; i++)
                line[x++] = pixel;
        }

        return x;
    }
}
