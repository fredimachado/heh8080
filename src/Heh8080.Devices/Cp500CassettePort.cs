using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Model III cassette port at FFh with no tape attached: reads return 00h and
/// writes are discarded.
/// </summary>
public sealed class Cp500CassettePort : IIoDevice
{
    public const byte Port = 0xFF;

    /// <summary>Registers this stub at FFh.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, Port);
    }

    public byte In(byte port) => 0x00;

    public void Out(byte port, byte value)
    {
    }
}
