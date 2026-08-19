using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>Minimal host-facing serial endpoint for the optional CP-500 CP532C adapter.</summary>
public interface ICp500Rs232Endpoint
{
    /// <summary>Whether a byte can be read without blocking.</summary>
    bool IsInputReady { get; }

    /// <summary>Whether a byte can be written without blocking.</summary>
    bool IsOutputReady { get; }

    /// <summary>Reads one queued byte.</summary>
    byte ReadByte();

    /// <summary>Writes one byte.</summary>
    void WriteByte(byte value);
}

/// <summary>
/// Safe RS-232 stub at E8h-EBh. It provides data and ready bits without assuming a UART model.
/// </summary>
public sealed class Cp500Rs232Device : IIoDevice
{
    /// <summary>First serial-adapter port.</summary>
    public const byte StartPort = 0xE8;

    /// <summary>Last serial-adapter port.</summary>
    public const byte EndPort = 0xEB;

    private readonly ICp500Rs232Endpoint? _endpoint;
    private byte _control;
    private byte _command;

    /// <summary>Creates an optional serial adapter. A null endpoint remains an output-ready stub.</summary>
    public Cp500Rs232Device(ICp500Rs232Endpoint? endpoint = null)
    {
        _endpoint = endpoint;
    }

    /// <summary>Last value written to the control register EAh.</summary>
    public byte Control => _control;

    /// <summary>Last value written to the command register EBh.</summary>
    public byte Command => _command;

    /// <summary>Registers the adapter at E8h-EBh.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, StartPort, EndPort);
    }

    public byte In(byte port) => port switch
    {
        0xE8 => _endpoint?.IsInputReady == true ? _endpoint.ReadByte() : (byte)0xFF,
        0xE9 => GetStatus(),
        0xEA => _control,
        0xEB => _command,
        _ => 0xFF
    };

    public void Out(byte port, byte value)
    {
        switch (port)
        {
            case 0xE8:
                if (_endpoint?.IsOutputReady ?? true)
                    _endpoint?.WriteByte(value);
                break;
            case 0xEA:
                _control = value;
                break;
            case 0xEB:
                _command = value;
                break;
        }
    }

    private byte GetStatus()
    {
        byte status = 0;
        if (_endpoint?.IsInputReady == true)
            status |= 0x01;
        if (_endpoint?.IsOutputReady ?? true)
            status |= 0x02;
        return status;
    }
}
