using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500KeySequenceTests
{
    [Fact]
    public void LettersTakeShiftFromTheirCaseAndPunctuationFromTheTrs80Keyboard()
    {
        IReadOnlyList<Cp500KeyPress> keys = Cp500KeySequence.Parse("aB1!@ ");

        Assert.Equal(
            new[]
            {
                new Cp500KeyPress(0, 1, shift: false),
                new Cp500KeyPress(0, 2, shift: true),
                new Cp500KeyPress(4, 1, shift: false),
                new Cp500KeyPress(4, 1, shift: true),
                new Cp500KeyPress(0, 0, shift: false),
                new Cp500KeyPress(6, 7, shift: false)
            },
            keys);
    }

    [Fact]
    public void BackslashRAndBracedNamesAreKeysWithoutCharacters()
    {
        IReadOnlyList<Cp500KeyPress> keys = Cp500KeySequence.Parse(@"\r{ENTER}{clear}{BREAK}{UP}{DOWN}{LEFT}{RIGHT}{SPACE}" + "\r");

        Assert.Equal(
            new[]
            {
                new Cp500KeyPress(6, 0, shift: false),
                new Cp500KeyPress(6, 0, shift: false),
                new Cp500KeyPress(6, 1, shift: false),
                new Cp500KeyPress(6, 2, shift: false),
                new Cp500KeyPress(6, 3, shift: false),
                new Cp500KeyPress(6, 4, shift: false),
                new Cp500KeyPress(6, 5, shift: false),
                new Cp500KeyPress(6, 6, shift: false),
                new Cp500KeyPress(6, 7, shift: false),
                new Cp500KeyPress(6, 0, shift: false)
            },
            keys);
    }

    [Theory]
    [InlineData(@"\n", @"Unknown escape '\n' at position 0; use \r or \\.")]
    [InlineData(@"ab\", @"The text ends with a lone '\'; write '\\' for a backslash.")]
    [InlineData("{HOME}", "Unknown key '{HOME}' at position 0; known keys: ENTER, CLEAR, BREAK, UP, DOWN, LEFT, RIGHT, SPACE, BS, TAB.")]
    [InlineData("x{ENTER", "Unclosed '{' at position 1.")]
    [InlineData("a^", "The CP-500 keyboard has no key for '^' (position 1).")]
    [InlineData(@"\\", @"The CP-500 keyboard has no key for '\' (position 1).")]
    [InlineData("a\n", "The CP-500 keyboard has no key for U+000A (position 1).")]
    public void RejectsTextTheKeyboardCannotType(string text, string message)
    {
        var error = Assert.Throws<FormatException>(() => Cp500KeySequence.Parse(text));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void KeyPressRejectsPositionsOutsideTheMatrix()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Cp500KeyPress(8, 0, shift: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Cp500KeyPress(0, -1, shift: false));
    }
}
