using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>CP-500 parallel-printer data port at F8h.</summary>
public sealed class Cp500PrinterPort : IIoDevice
{
    /// <summary>Printer data/status port.</summary>
    public const byte Port = 0xF8;

    private readonly IPrinterDevice? _printer;

    /// <summary>Creates a printer port. A null printer behaves as an always-ready sink.</summary>
    public Cp500PrinterPort(IPrinterDevice? printer = null)
    {
        _printer = printer;
    }

    /// <summary>Registers this device at F8h.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, Port);
    }

    public byte In(byte port) => port == Port && (_printer?.IsReady ?? true) ? (byte)0xFF : (byte)0x00;

    public void Out(byte port, byte value)
    {
        if (port == Port)
            _printer?.WriteChar(value);
    }
}
