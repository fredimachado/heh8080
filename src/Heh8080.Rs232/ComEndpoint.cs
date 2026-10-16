using System.IO.Ports;
using Heh8080.Devices;

namespace Heh8080.Rs232;

/// <summary>
/// Host serial port. CTS, DSR, and carrier are the levels the OS reports.
/// Ring and break are edges: <see cref="SerialPort"/> has no level for either.
/// </summary>
internal sealed class ComEndpoint : ISerialEndpoint
{
    private readonly SerialPort _port;
    private bool _ring;
    private bool _break;

    private ComEndpoint(SerialPort port)
    {
        _port = port;
        _port.PinChanged += OnPin;
    }

    public static ComEndpoint Open(string name)
    {
        var port = new SerialPort(name);
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            port.Dispose();
            throw new IOException($"RS-232 COM {name} could not be opened: {ex.Message}", ex);
        }

        return new ComEndpoint(port);
    }

    public ModemInputs Inputs
    {
        get
        {
            bool ring = _ring;
            _ring = false;
            try
            {
                return new ModemInputs(_port.CtsHolding, _port.DsrHolding, _port.CDHolding, ring, !_break);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return ModemInputs.Idle;
            }
        }
    }

    public bool TryRead(out byte value)
    {
        try
        {
            if (_port.BytesToRead == 0)
            {
                value = 0;
                return false;
            }

            value = (byte)_port.ReadByte();
            _break = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            value = 0;
            return false;
        }
    }

    public void Write(byte value)
    {
        try
        {
            _port.Write([value], 0, 1);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
        }
    }

    public void AdvanceCycles(long cycles)
    {
    }

    public void Dispose()
    {
        _port.PinChanged -= OnPin;
        try
        {
            if (_port.IsOpen)
                _port.Close();
        }
        catch (IOException)
        {
        }

        _port.Dispose();
    }

    private void OnPin(object? sender, SerialPinChangedEventArgs e)
    {
        if (e.EventType == SerialPinChange.Ring)
            _ring = true;
        else if (e.EventType == SerialPinChange.Break)
            _break = true;
    }
}
