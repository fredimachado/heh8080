using System.Text;

namespace Heh8080.Devices;

/// <summary>
/// Reads the CP-500 screen as Unicode text, following what <see cref="Cp500Raster"/> draws: codes 00h-1Fh are the
/// character generator's European letters, 20h-7Fh ASCII, 80h-BFh 2-by-3 block graphics (Unicode sextants),
/// and C0h-FFh the special symbols or, with ECh bit 3, half-width katakana. A glyph with no Unicode counterpart
/// reads as U+FFFD. In 32-column mode only the even video columns are visible, so only they are read.
/// </summary>
public static class Cp500ScreenText
{
    // Read off the glyph bitmaps of the character generator dump; U+FFFD marks glyphs with no Unicode counterpart.
    private const string LowGlyphs =
        " £|éÜÂ¬ÖØùñ`â\uFFFDÄÃ" +
        "ÑöøÕßüõ\uFFFDäàá§É\uFFFDÇ~";

    // Glyphs 80h-BFh, which codes C0h-FFh show with the special (non-katakana) set.
    private const string SpecialGlyphs =
        "♠♥♦♣☺☹≤≥αβγδεζηθ" +
        "ικλμνξοπρστυφχψω" +
        "Ω√÷Σ≈Δ∫≠\uFFFD‰\uFFFD∞✓§❖©" +
        "\uFFFD¶¢®\uFFFD\uFFFD\uFFFD℞\uFFFD♂♀\uFFFD☻\uFFFD\uFFFD\uFFFD";

    private const int FirstSextant = 0x1FB00;
    private const int LeftColumn = 0x15;
    private const int RightColumn = 0x2A;
    private const int AllBlocks = 0x3F;
    private const char Yen = '¥';
    private const int FirstHalfWidthKatakana = 0xFF61;

    /// <summary>The character a screen cell shows for a video code under the given character set.</summary>
    public static Rune Character(byte code, bool alternateCharacterSet) => code switch
    {
        < 0x20 => new Rune(LowGlyphs[code]),
        0x7C => new Rune('¦'),
        0x7F => new Rune('±'),
        < 0x80 => new Rune(code),
        < 0xC0 => Blocks(code & AllBlocks),
        0xC0 when alternateCharacterSet => new Rune(Yen),
        _ when alternateCharacterSet => new Rune(FirstHalfWidthKatakana + code - 0xC1),
        _ => new Rune(SpecialGlyphs[code - 0xC0])
    };

    /// <summary>
    /// Reads every visible row of the screen, untrimmed: 64 cells per row, or 32 in 32-column mode.
    /// Safe to call while the CPU runs: a cell changing mid-copy reads as either its old or new value.
    /// </summary>
    public static IReadOnlyList<string> ReadRows(Cp500VideoDevice video)
    {
        ArgumentNullException.ThrowIfNull(video);
        byte[] videoRam = video.VideoRam.ToArray();
        bool alternate = video.AlternateCharacterSet;
        int step = video.Is32ColumnMode ? 2 : 1;

        var rows = new string[Cp500VideoDevice.Rows];
        var text = new StringBuilder(Cp500VideoDevice.Columns * 2);
        for (int row = 0; row < rows.Length; row++)
        {
            text.Clear();
            for (int column = 0; column < Cp500VideoDevice.Columns; column += step)
                text.Append(Character(videoRam[row * Cp500VideoDevice.Columns + column], alternate).ToString());

            rows[row] = text.ToString();
        }

        return rows;
    }

    /// <summary>Formats rows for printing: trailing spaces trimmed from each row, rows joined by '\n'.</summary>
    public static string Format(IReadOnlyList<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return string.Join('\n', rows.Select(row => row.TrimEnd(' ')));
    }

    /// <summary>Bits 0-5 are top left, top right, middle left, middle right, bottom left, bottom right, as in Unicode's sextant order.</summary>
    private static Rune Blocks(int cells) => cells switch
    {
        0 => new Rune(' '),
        LeftColumn => new Rune('▌'),
        RightColumn => new Rune('▐'),
        AllBlocks => new Rune('█'),
        _ => new Rune(FirstSextant + cells - 1 - (cells > LeftColumn ? 1 : 0) - (cells > RightColumn ? 1 : 0))
    };
}
