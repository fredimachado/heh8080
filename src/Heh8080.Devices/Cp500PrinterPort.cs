using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>CP-500 (Model III) parallel-printer port, mirrored across F8h-FBh.</summary>
public sealed class Cp500PrinterPort : IIoDevice
{
    /// <summary>First printer data/status port.</summary>
    public const byte Port = 0xF8;

    /// <summary>Last mirrored printer port.</summary>
    public const byte LastPort = 0xFB;

    /// <summary>Bit 5 SELECT and bit 4 no-FAULT: the pattern Model III software treats as ready.</summary>
    private const byte ReadyStatus = 0x30;

    /// <summary>Bit 7 BUSY.</summary>
    private const byte Busy = 0x80;

    private readonly IPrinterDevice? _printer;

    /// <summary>Creates a printer port. A null printer behaves as an always-ready sink.</summary>
    public Cp500PrinterPort(IPrinterDevice? printer = null)
    {
        _printer = printer;
    }

    /// <summary>Registers this device at F8h-FBh.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, Port, LastPort);
    }

    public byte In(byte port) => (_printer?.IsReady ?? true) ? ReadyStatus : (byte)(ReadyStatus | Busy);

    public void Out(byte port, byte value) => _printer?.WriteChar(value);
}
