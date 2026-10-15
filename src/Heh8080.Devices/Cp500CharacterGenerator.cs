namespace Heh8080.Devices;

/// <summary>
/// The CP-500 character generator ROM: 256 glyphs of 8 scanlines, one byte per scanline with bit 7 leftmost.
/// It is a user-supplied image, never embedded.
/// </summary>
public sealed class Cp500CharacterGenerator
{
    /// <summary>Scanlines stored per glyph.</summary>
    public const int GlyphHeight = 8;

    /// <summary>Size of the ROM image in bytes: 256 glyphs of <see cref="GlyphHeight"/> bytes.</summary>
    public const int ImageSize = 256 * GlyphHeight;

    private readonly byte[] _image;

    /// <summary>Copies a character ROM image, which must be exactly <see cref="ImageSize"/> bytes.</summary>
    public Cp500CharacterGenerator(ReadOnlySpan<byte> image)
    {
        if (image.Length != ImageSize)
            throw new ArgumentException($"A CP-500 character ROM image is exactly {ImageSize} bytes.", nameof(image));

        _image = image.ToArray();
    }

    /// <summary>Returns one scanline (0-7) of the glyph stored under a character code.</summary>
    public byte GetScanline(int code, int scanline)
    {
        if ((uint)code > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(code));
        if ((uint)scanline >= GlyphHeight)
            throw new ArgumentOutOfRangeException(nameof(scanline));

        return _image[code * GlyphHeight + scanline];
    }
}
