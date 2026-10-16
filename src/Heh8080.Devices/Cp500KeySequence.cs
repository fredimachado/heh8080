namespace Heh8080.Devices;

/// <summary>One key an automated typist presses: a matrix switch and whether SHIFT is held with it.</summary>
public readonly record struct Cp500KeyPress
{
    /// <summary>Creates a key press. Row and column values are in the range 0-7.</summary>
    public Cp500KeyPress(int row, int column, bool shift)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row, 7);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(column, 7);
        Row = row;
        Column = column;
        Shift = shift;
    }

    /// <summary>Matrix row, 0-7.</summary>
    public int Row { get; }

    /// <summary>Matrix column, 0-7.</summary>
    public int Column { get; }

    /// <summary>Whether SHIFT is held together with the key.</summary>
    public bool Shift { get; }
}

/// <summary>
/// Parses text to type into key presses. Characters map through <see cref="Cp500KeyMap"/> with the SHIFT a desktop
/// user would send: an upper-case letter is SHIFT plus the letter, a lower-case letter is the letter alone, and
/// punctuation takes the SHIFT the TRS-80 keyboard needs. <c>\r</c> is ENTER and <c>\\</c> a backslash; braces name
/// keys without a character: <c>{ENTER}</c>, <c>{CLEAR}</c>, <c>{BREAK}</c>, <c>{UP}</c>, <c>{DOWN}</c>,
/// <c>{LEFT}</c>, <c>{RIGHT}</c> and <c>{SPACE}</c>, in any letter case.
/// </summary>
public static class Cp500KeySequence
{
    private static readonly (string Name, Cp500HostKey Key)[] NamedKeys =
    [
        ("ENTER", Cp500HostKey.Enter),
        ("CLEAR", Cp500HostKey.Home),
        ("BREAK", Cp500HostKey.Escape),
        ("UP", Cp500HostKey.Up),
        ("DOWN", Cp500HostKey.Down),
        ("LEFT", Cp500HostKey.Left),
        ("RIGHT", Cp500HostKey.Right),
        ("SPACE", Cp500HostKey.Space)
    ];

    /// <summary>Names accepted inside braces.</summary>
    public static IEnumerable<string> KeyNames => NamedKeys.Select(entry => entry.Name);

    /// <summary>Parses text into the key presses that type it, in order.</summary>
    /// <exception cref="FormatException">
    /// The text has an unknown escape or key name, an unclosed brace, or a character the CP-500 keyboard cannot type.
    /// </exception>
    public static IReadOnlyList<Cp500KeyPress> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var keys = new List<Cp500KeyPress>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\\':
                    keys.Add(Escape(text, ++i));
                    break;
                case '{':
                    int close = text.IndexOf('}', i + 1);
                    if (close < 0)
                        throw new FormatException($"Unclosed '{{' at position {i}.");
                    keys.Add(Named(text[(i + 1)..close], i));
                    i = close;
                    break;
                default:
                    keys.Add(Character(text[i], i));
                    break;
            }
        }

        return keys;
    }

    private static Cp500KeyPress Escape(string text, int position)
    {
        if (position >= text.Length)
            throw new FormatException("The text ends with a lone '\\'; write '\\\\' for a backslash.");

        return text[position] switch
        {
            'r' => Press(Cp500KeyMap.Map(Cp500HostKey.Enter), shift: false),
            '\\' => Character('\\', position),
            char other => throw new FormatException($"Unknown escape '\\{other}' at position {position - 1}; use \\r or \\\\.")
        };
    }

    private static Cp500KeyPress Named(string name, int position)
    {
        foreach (var (keyName, key) in NamedKeys)
        {
            if (string.Equals(keyName, name, StringComparison.OrdinalIgnoreCase))
                return Press(Cp500KeyMap.Map(key), shift: false);
        }

        throw new FormatException(
            $"Unknown key '{{{name}}}' at position {position}; known keys: {string.Join(", ", KeyNames)}.");
    }

    private static Cp500KeyPress Character(char character, int position)
    {
        if (!Cp500KeyMap.TryMapCharacter(character, out Cp500KeyStroke stroke))
            throw new FormatException($"The CP-500 keyboard has no key for '{character}' (position {position}).");

        bool shift = stroke.Shift switch
        {
            Cp500ShiftMode.Shifted => true,
            Cp500ShiftMode.Unshifted => false,
            Cp500ShiftMode.Host => char.IsUpper(character),
            _ => throw new InvalidOperationException($"Unknown shift mode {stroke.Shift}.")
        };
        return Press(stroke, shift);
    }

    private static Cp500KeyPress Press(Cp500KeyStroke stroke, bool shift) => new(stroke.Row, stroke.Column, shift);
}
