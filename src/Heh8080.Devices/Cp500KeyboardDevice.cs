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

    private static readonly Cp500KeyStroke Break = Cp500KeyMap.Map(Cp500HostKey.Escape);

    /// <summary>Matrix of the keys the host user holds. The memory region ORs it with the automatic BREAK.</summary>
    public KeyboardMatrix Matrix { get; } = new();

    /// <summary>Keys the machine itself holds, so host key events and focus loss can never touch them.</summary>
    public KeyboardMatrix Automatic { get; } = new();

    /// <summary>Updates one matrix switch. Row and column values are in the range 0-7.</summary>
    public void SetKeyState(int row, int column, bool pressed) => Matrix.SetKeyState(row, column, pressed);

    /// <summary>Releases all host keys, for example after host focus is lost. An automatic BREAK stays down.</summary>
    public void ReleaseAll() => Matrix.Clear();

    /// <summary>Holds or releases BREAK independently of the host keys.</summary>
    public void SetAutomaticBreak(bool pressed) => Automatic.SetKeyState(Break.Row, Break.Column, pressed);
}
