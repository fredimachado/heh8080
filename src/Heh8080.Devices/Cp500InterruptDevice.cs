using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// CP-500 heartbeat interrupt latch at port FFh.
/// A scheduler calls <see cref="Tick"/> at the configured 30 or 80 Hz rate.
/// </summary>
public sealed class Cp500InterruptDevice : IIoDevice
{
    /// <summary>Interrupt/cassette status and control port.</summary>
    public const byte Port = 0xFF;

    private Action? _interruptCallback;
    private byte _controlLatch;

    /// <summary>Creates a 30 Hz or 80 Hz interrupt source.</summary>
    public Cp500InterruptDevice(int hertz = 30)
    {
        if (hertz is not 30 and not 80)
            throw new ArgumentOutOfRangeException(nameof(hertz), "CP-500 heartbeat frequency must be 30 or 80 Hz.");

        Hertz = hertz;
        Enabled = true;
    }

    /// <summary>Requested scheduler frequency.</summary>
    public int Hertz { get; }

    /// <summary>Whether scheduler ticks may assert the interrupt latch.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Whether an interrupt is waiting to be acknowledged by a status read.</summary>
    public bool InterruptPending { get; private set; }

    /// <summary>Last value written to the control latch.</summary>
    public byte ControlLatch => _controlLatch;

    /// <summary>Registers an action that presents an interrupt to the CPU.</summary>
    public void SetInterruptCallback(Action? callback) => _interruptCallback = callback;

    /// <summary>Raises one heartbeat interrupt when enabled.</summary>
    public void Tick()
    {
        if (!Enabled)
            return;

        InterruptPending = true;
        _interruptCallback?.Invoke();
    }

    /// <summary>Clears the pending status without changing enable state.</summary>
    public void Acknowledge() => InterruptPending = false;

    /// <summary>Restores the reset state expected by the CP-500 heartbeat hardware.</summary>
    public void Reset()
    {
        _controlLatch = 0;
        Enabled = true;
        InterruptPending = false;
    }

    /// <summary>Registers this device at FFh.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, Port);
    }

    public byte In(byte port)
    {
        if (port != Port)
            return 0xFF;

        byte status = (byte)(_controlLatch & 0x7E);
        if (Enabled)
            status |= 0x01;
        if (InterruptPending)
            status |= 0x80;

        Acknowledge();
        return status;
    }

    public void Out(byte port, byte value)
    {
        if (port != Port)
            return;

        _controlLatch = value;
        Enabled = (value & 0x01) != 0;
        if ((value & 0x80) != 0)
            Acknowledge();
    }
}
