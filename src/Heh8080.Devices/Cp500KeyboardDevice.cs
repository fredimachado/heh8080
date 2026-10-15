using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Host-facing CP-500 8-by-8 active-high keyboard matrix.
/// The machine maps this device over 3800h-3BFFh.
/// </summary>
public sealed class Cp500KeyboardDevice
{
    /// <summary>Start address of the Model III keyboard range.</summary>
    public const ushort StartAddress = 0x3800;

    /// <summary>Size of the mirrored keyboard range.</summary>
    public const int Length = 0x400;

    /// <summary>Underlying matrix shared by the memory region.</summary>
    public KeyboardMatrix Matrix { get; } = new();

    /// <summary>Updates one matrix switch. Row and column values are in the range 0-7.</summary>
    public void SetKeyState(int row, int column, bool pressed) => Matrix.SetKeyState(row, column, pressed);

    /// <summary>Releases all keys, for example after host focus is lost.</summary>
    public void ReleaseAll() => Matrix.Clear();
}
