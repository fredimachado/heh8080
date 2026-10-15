namespace Heh8080.Devices;

/// <summary>Which SHIFT state the TRS-80 should see while a key is held.</summary>
public enum Cp500ShiftMode
{
    /// <summary>Follow the host SHIFT keys.</summary>
    Host,

    /// <summary>Force SHIFT pressed, whatever the host keyboard has.</summary>
    Shifted,

    /// <summary>Force SHIFT released, whatever the host keyboard has.</summary>
    Unshifted
}

/// <summary>A matrix switch plus the SHIFT state it needs.</summary>
public readonly record struct Cp500KeyStroke(int Row, int Column, Cp500ShiftMode Shift)
{
    /// <summary>Whether this stroke is the SHIFT key itself.</summary>
    public bool IsShift => Row == ShiftRow && Column == ShiftColumn;

    internal const int ShiftRow = 7;
    internal const int ShiftColumn = 0;
}

/// <summary>Host keys that do not produce a character on the TRS-80 keyboard.</summary>
public enum Cp500HostKey
{
    Shift,
    Enter,
    Space,
    Backspace,
    Escape,
    Home,
    Up,
    Down,
    Left,
    Right
}

/// <summary>
/// Maps host keys and characters to Model III keyboard matrix switches.
/// Row 6 is ENTER, CLEAR, BREAK, UP, DOWN, LEFT, RIGHT, SPACE from bit 0 up; row 7 bit 0 is SHIFT.
/// The ROM decodes UP as "[" and has no key that produces "\", "]", "^" or "_".
/// </summary>
public static class Cp500KeyMap
{
    /// <summary>Maps a non-text host key. Backspace is the LEFT arrow, which erases on the Model III.</summary>
    public static Cp500KeyStroke Map(Cp500HostKey key) => key switch
    {
        Cp500HostKey.Shift => new(Cp500KeyStroke.ShiftRow, Cp500KeyStroke.ShiftColumn, Cp500ShiftMode.Host),
        Cp500HostKey.Enter => new(6, 0, Cp500ShiftMode.Host),
        Cp500HostKey.Home => new(6, 1, Cp500ShiftMode.Host),
        Cp500HostKey.Escape => new(6, 2, Cp500ShiftMode.Host),
        Cp500HostKey.Up => new(6, 3, Cp500ShiftMode.Host),
        Cp500HostKey.Down => new(6, 4, Cp500ShiftMode.Host),
        Cp500HostKey.Left or Cp500HostKey.Backspace => new(6, 5, Cp500ShiftMode.Host),
        Cp500HostKey.Right => new(6, 6, Cp500ShiftMode.Host),
        Cp500HostKey.Space => new(6, 7, Cp500ShiftMode.Host),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown host key.")
    };

    /// <summary>
    /// Maps the character a host key produced. Letters follow the host SHIFT; every other character
    /// states the SHIFT it needs on the TRS-80, which differs from a host keyboard for punctuation.
    /// </summary>
    public static bool TryMapCharacter(char character, out Cp500KeyStroke stroke)
    {
        char c = char.ToUpperInvariant(character);
        stroke = c switch
        {
            '@' => new(0, 0, Cp500ShiftMode.Unshifted),
            >= 'A' and <= 'G' => new(0, c - 'A' + 1, Cp500ShiftMode.Host),
            >= 'H' and <= 'O' => new(1, c - 'H', Cp500ShiftMode.Host),
            >= 'P' and <= 'W' => new(2, c - 'P', Cp500ShiftMode.Host),
            >= 'X' and <= 'Z' => new(3, c - 'X', Cp500ShiftMode.Host),
            '[' => new(6, 3, Cp500ShiftMode.Unshifted),
            >= '0' and <= '7' => new(4, c - '0', Cp500ShiftMode.Unshifted),
            '8' or '9' => new(5, c - '8', Cp500ShiftMode.Unshifted),
            ':' => new(5, 2, Cp500ShiftMode.Unshifted),
            ';' => new(5, 3, Cp500ShiftMode.Unshifted),
            ',' => new(5, 4, Cp500ShiftMode.Unshifted),
            '-' => new(5, 5, Cp500ShiftMode.Unshifted),
            '.' => new(5, 6, Cp500ShiftMode.Unshifted),
            '/' => new(5, 7, Cp500ShiftMode.Unshifted),
            ' ' => new(6, 7, Cp500ShiftMode.Host),
            >= '!' and <= '\'' => new(4, c - '!' + 1, Cp500ShiftMode.Shifted),
            '(' or ')' => new(5, c - '(', Cp500ShiftMode.Shifted),
            '*' => new(5, 2, Cp500ShiftMode.Shifted),
            '+' => new(5, 3, Cp500ShiftMode.Shifted),
            '<' => new(5, 4, Cp500ShiftMode.Shifted),
            '=' => new(5, 5, Cp500ShiftMode.Shifted),
            '>' => new(5, 6, Cp500ShiftMode.Shifted),
            '?' => new(5, 7, Cp500ShiftMode.Shifted),
            _ => new(-1, -1, Cp500ShiftMode.Host)
        };
        return stroke.Row >= 0;
    }
}
