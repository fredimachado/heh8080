using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Host-facing CP-500 8-by-8 active-low keyboard matrix.
/// The machine maps this device over 3800h-3BFFh.
/// </summary>
public sealed class Cp500KeyboardDevice
{
    private readonly byte[] _pressedColumns = new byte[8];
    private int _pressedKeyCount;

    /// <summary>Start address of the Model III keyboard range.</summary>
    public const ushort StartAddress = 0x3800;

    /// <summary>Size of the mirrored keyboard range.</summary>
    public const int Length = 0x400;

    /// <summary>Underlying matrix shared by the memory region.</summary>
    public KeyboardMatrix Matrix { get; } = new();

    /// <summary>Raised when the host changes a matrix switch.</summary>
    public event Action<int, int, bool>? KeyStateChanged;

    /// <summary>Whether at least one host key is currently asserted in the matrix.</summary>
    public bool HasPressedKeys => Volatile.Read(ref _pressedKeyCount) != 0;

    /// <summary>Updates one matrix switch. Row and column values are in the range 0-7.</summary>
    public void SetKeyState(int row, int column, bool pressed)
    {
        if ((uint)row >= 8 || (uint)column >= 8)
            throw new ArgumentOutOfRangeException(nameof(row));

        byte mask = (byte)(1 << column);
        bool wasPressed = (_pressedColumns[row] & mask) != 0;
        if (wasPressed == pressed)
            return;

        _pressedColumns[row] = pressed
            ? (byte)(_pressedColumns[row] | mask)
            : (byte)(_pressedColumns[row] & ~mask);
        Interlocked.Add(ref _pressedKeyCount, pressed ? 1 : -1);
        Matrix.SetKeyState(row, column, pressed);
        KeyStateChanged?.Invoke(row, column, pressed);
    }

    /// <summary>Releases all keys, for example after host focus is lost.</summary>
    public void ReleaseAll()
    {
        Array.Clear(_pressedColumns);
        Interlocked.Exchange(ref _pressedKeyCount, 0);
        Matrix.Clear();
    }

    /// <summary>Reads selected matrix rows using the low byte of a CP-500 address.</summary>
    public byte Read(ushort address) => Matrix.Read((byte)address);
}
