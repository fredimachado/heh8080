namespace Heh8080.Devices;

/// <summary>
/// Forwards host key presses and releases to the CP-500 keyboard matrix. A host key holds its matrix switch
/// for exactly as long as it is down, so software sees real key state and can auto-repeat or poll held keys.
/// Not thread safe: call from the host UI thread only.
/// </summary>
public sealed class Cp500HostKeyboard
{
    private readonly Cp500KeyboardDevice _keyboard;
    private readonly List<(int HostKey, Cp500KeyStroke Stroke)> _held = new();

    /// <summary>Creates a forwarder for one keyboard.</summary>
    public Cp500HostKeyboard(Cp500KeyboardDevice keyboard)
    {
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
    }

    /// <summary>Presses a stroke for a host key. Host auto-repeat of a key already down is ignored.</summary>
    public void KeyDown(int hostKey, Cp500KeyStroke stroke)
    {
        if (_held.Exists(entry => entry.HostKey == hostKey))
            return;

        _held.Add((hostKey, stroke));
        Apply();
    }

    /// <summary>Releases whatever a host key pressed. Returns whether the key was down.</summary>
    public bool KeyUp(int hostKey)
    {
        if (_held.RemoveAll(entry => entry.HostKey == hostKey) == 0)
            return false;

        Apply();
        return true;
    }

    /// <summary>Releases every key, for example after the host window loses focus.</summary>
    public void ReleaseAll()
    {
        _held.Clear();
        _keyboard.ReleaseAll();
    }

    /// <summary>Makes the matrix show exactly the held strokes, changing only the switches that differ.</summary>
    private void Apply()
    {
        Span<byte> rows = stackalloc byte[8];
        foreach (var (_, stroke) in _held)
        {
            if (!stroke.IsShift)
                rows[stroke.Row] |= (byte)(1 << stroke.Column);
        }

        if (ShiftPressed())
            rows[Cp500KeyStroke.ShiftRow] |= 1 << Cp500KeyStroke.ShiftColumn;

        for (int row = 0; row < rows.Length; row++)
        {
            byte changed = (byte)(rows[row] ^ _keyboard.Matrix.ReadRow(row));
            for (int column = 0; column < 8; column++)
            {
                if ((changed & (1 << column)) != 0)
                    _keyboard.SetKeyState(row, column, (rows[row] & (1 << column)) != 0);
            }
        }
    }

    /// <summary>The most recently pressed key that dictates SHIFT wins, otherwise a held host SHIFT key does.</summary>
    private bool ShiftPressed()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            var mode = _held[i].Stroke.Shift;
            if (!_held[i].Stroke.IsShift && mode != Cp500ShiftMode.Host)
                return mode == Cp500ShiftMode.Shifted;
        }

        return _held.Exists(entry => entry.Stroke.IsShift);
    }
}
