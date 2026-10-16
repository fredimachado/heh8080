using System.Text;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500ScreenTextTests
{
    [Theory]
    [InlineData(0x20, " ")]
    [InlineData(0x41, "A")]
    [InlineData(0x7E, "~")]
    [InlineData(0x7C, "¦")]
    [InlineData(0x7F, "±")]
    [InlineData(0x00, " ")]
    [InlineData(0x01, "£")]
    [InlineData(0x03, "é")]
    [InlineData(0x0F, "Ã")]
    [InlineData(0x1E, "Ç")]
    public void CodesBelow80hAreAsciiAndTheEuropeanLetters(byte code, string expected)
    {
        Assert.Equal(expected, Cp500ScreenText.Character(code, alternateCharacterSet: false).ToString());
        Assert.Equal(expected, Cp500ScreenText.Character(code, alternateCharacterSet: true).ToString());
    }

    [Theory]
    [InlineData(0x80, 0x0020)]
    [InlineData(0x81, 0x1FB00)]
    [InlineData(0x95, 0x258C)]
    [InlineData(0x96, 0x1FB14)]
    [InlineData(0xAA, 0x2590)]
    [InlineData(0xBE, 0x1FB3B)]
    [InlineData(0xBF, 0x2588)]
    public void Codes80hToBFhAreSextantBlocksInEitherCharacterSet(byte code, int expected)
    {
        Assert.Equal(expected, Cp500ScreenText.Character(code, alternateCharacterSet: false).Value);
        Assert.Equal(expected, Cp500ScreenText.Character(code, alternateCharacterSet: true).Value);
    }

    [Theory]
    [InlineData(0xC0, false, 0x2660)]
    [InlineData(0xC8, false, 0x03B1)]
    [InlineData(0xFF, false, 0xFFFD)]
    [InlineData(0xC0, true, 0x00A5)]
    [InlineData(0xC1, true, 0xFF61)]
    [InlineData(0xD1, true, 0xFF71)]
    [InlineData(0xFF, true, 0xFF9F)]
    public void CodesC0hToFFhAreSpecialSymbolsOrHalfWidthKatakana(byte code, bool alternate, int expected)
    {
        Assert.Equal(expected, Cp500ScreenText.Character(code, alternate).Value);
    }

    [Fact]
    public void ReadRowsReturnsSixteenUntrimmedRowsOfSixtyFourCells()
    {
        var video = new Cp500VideoDevice();
        Write(video, 0, 0, "HELLO");
        Write(video, 3, 15, "\u0003\u0081");

        IReadOnlyList<string> rows = Cp500ScreenText.ReadRows(video);

        Assert.Equal(16, rows.Count);
        Assert.Equal("HELLO" + new string(' ', 59), rows[0]);
        Assert.Equal(new string(' ', 64), rows[1]);
        Assert.Equal("   é\U0001FB00" + new string(' ', 59), rows[15]);
    }

    [Fact]
    public void ThirtyTwoColumnModeReadsOnlyTheEvenColumns()
    {
        var video = new Cp500VideoDevice();
        Write(video, 0, 0, "HxExLxLxOx");
        video.Out(Cp500VideoDevice.ControlPort, Cp500VideoDevice.ThirtyTwoColumnBit);

        IReadOnlyList<string> rows = Cp500ScreenText.ReadRows(video);

        Assert.Equal("HELLO" + new string(' ', 27), rows[0]);
    }

    [Fact]
    public void FormatTrimsTrailingSpacesAndJoinsRowsWithNewlines()
    {
        string text = Cp500ScreenText.Format(["  A  ", "", " B"]);

        Assert.Equal("  A\n\n B", text);
    }

    private static void Write(Cp500VideoDevice video, int column, int row, string text)
    {
        byte[] codes = Encoding.Latin1.GetBytes(text);
        for (int i = 0; i < codes.Length; i++)
            video.VideoRam.Write((ushort)(Cp500VideoDevice.StartAddress + row * Cp500VideoDevice.Columns + column + i), codes[i]);
    }
}
