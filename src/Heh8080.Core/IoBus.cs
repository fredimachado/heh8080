namespace Heh8080.Core;

/// <summary>
/// I/O device interface for port handlers.
/// </summary>
public interface IIoDevice
{
    /// <summary>
    /// Handle IN instruction from the specified port.
    /// </summary>
    byte In(byte port);

    /// <summary>
    /// Handle OUT instruction to the specified port.
    /// </summary>
    void Out(byte port, byte value);
}

/// <summary>
/// I/O bus that dispatches port operations to registered devices.
/// </summary>
public sealed class IoBus : IIoBus
{
    private readonly IIoDevice?[] _inputDevices = new IIoDevice?[256];
    private readonly IIoDevice?[] _outputDevices = new IIoDevice?[256];

    /// <summary>
    /// Register a device to handle a range of ports.
    /// </summary>
    public void Register(IIoDevice device, byte startPort, byte endPort)
    {
        for (int port = startPort; port <= endPort; port++)
        {
            _inputDevices[port] = device;
            _outputDevices[port] = device;
        }
    }

    /// <summary>
    /// Register a device to handle a single port.
    /// </summary>
    public void Register(IIoDevice device, byte port)
    {
        _inputDevices[port] = device;
        _outputDevices[port] = device;
    }

    /// <summary>
    /// Register a read-side device without changing the write-side handler.
    /// </summary>
    public void RegisterInput(IIoDevice device, byte startPort, byte endPort)
    {
        for (int port = startPort; port <= endPort; port++)
        {
            _inputDevices[port] = device;
        }
    }

    /// <summary>
    /// Register a write-side device without changing the read-side handler.
    /// </summary>
    public void RegisterOutput(IIoDevice device, byte startPort, byte endPort)
    {
        for (int port = startPort; port <= endPort; port++)
        {
            _outputDevices[port] = device;
        }
    }

    /// <summary>
    /// Unregister all ports for a device.
    /// </summary>
    public void Unregister(IIoDevice device)
    {
        for (int i = 0; i < 256; i++)
        {
            if (_inputDevices[i] == device)
                _inputDevices[i] = null;
            if (_outputDevices[i] == device)
                _outputDevices[i] = null;
        }
    }

    public byte In(byte port)
    {
        var device = _inputDevices[port];
        return device?.In(port) ?? 0xFF; // Unassigned ports return 0xFF
    }

    public void Out(byte port, byte value)
    {
        var device = _outputDevices[port];
        device?.Out(port, value);
    }
}
