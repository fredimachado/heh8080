using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// CP-500 16 KiB system EPROM at 0000h-3FFFh. Every IN from F4h-F7h toggles
/// a flip-flop that forces EPROM address line A11 high, so 3000h-37FFh reads
/// the monitor code that keyboard and video otherwise hide at 3800h-3FFFh.
/// </summary>
public sealed class Cp500SystemRom : MemoryRegion, IIoDevice
{
    public const byte FirstA11TogglePort = 0xF4;
    public const byte LastA11TogglePort = 0xF7;

    private const int A11 = 0x0800;
    private readonly byte[] _data = new byte[Cp500MachineProfile.SystemRomSizeBytes];
    private bool _a11Forced;

    /// <summary>Copies a ROM image, padding a short image with erased-EPROM FFh bytes.</summary>
    public Cp500SystemRom(ReadOnlySpan<byte> image)
        : base(0x0000, Cp500MachineProfile.SystemRomSizeBytes)
    {
        Array.Fill(_data, (byte)0xFF);
        image[..Math.Min(image.Length, _data.Length)].CopyTo(_data);
    }

    public override byte Read(ushort address) => _data[GetOffset(address) | (_a11Forced ? A11 : 0)];

    /// <summary>ROM writes are ignored.</summary>
    public override void Write(ushort address, byte value)
    {
    }

    /// <summary>Clears the A11 flip-flop.</summary>
    public void Reset() => _a11Forced = false;

    /// <summary>Registers the read side of F4h-F7h. Writes there select disk drives.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.RegisterInput(this, FirstA11TogglePort, LastA11TogglePort);
    }

    public byte In(byte port)
    {
        _a11Forced = !_a11Forced;
        return 0x00;
    }

    public void Out(byte port, byte value)
    {
    }
}
