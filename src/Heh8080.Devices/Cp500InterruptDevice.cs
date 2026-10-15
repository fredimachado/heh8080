using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// CP-500 / Model III interrupt fabric. E0h-E3h hold the IRQ mask and
/// active-low IRQ status, E4h-E7h the NMI mask and active-low NMI status,
/// and reads of ECh-EFh acknowledge the 30 Hz timer. Cassette sources are
/// accepted in the mask but never asserted.
/// </summary>
public sealed class Cp500InterruptDevice : IIoDevice
{
    public const byte FirstIrqPort = 0xE0;
    public const byte LastIrqPort = 0xE3;
    public const byte FirstNmiPort = 0xE4;
    public const byte LastNmiPort = 0xE7;
    public const byte FirstTimerAcknowledgePort = 0xEC;
    public const byte LastTimerAcknowledgePort = 0xEF;

    public const byte IrqCassetteRise = 0x01;
    public const byte IrqCassetteFall = 0x02;
    public const byte IrqTimer = 0x04;
    public const byte NmiReset = 0x20;
    public const byte NmiMotorTimeout = 0x40;
    public const byte NmiFdcIntrq = 0x80;

    private Action? _interruptCallback;
    private Action? _nmiCallback;
    private byte _irqMask;
    private byte _irqPending;
    private byte _nmiMask;
    private byte _nmiPending;
    private long _cycle;
    private long _timerDeadlineCycles;

    /// <summary>Creates a 30 Hz or 80 Hz timer source.</summary>
    public Cp500InterruptDevice(int hertz = 30, long clockHz = 2_027_520)
    {
        if (hertz is not 30 and not 80)
            throw new ArgumentOutOfRangeException(nameof(hertz), "CP-500 heartbeat frequency must be 30 or 80 Hz.");
        if (clockHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(clockHz));

        Hertz = hertz;
        ClockHz = clockHz;
        Reset();
    }

    /// <summary>Requested scheduler frequency.</summary>
    public int Hertz { get; }

    /// <summary>Emulated clock used for cycle-driven timer deadlines.</summary>
    public long ClockHz { get; }

    /// <summary>Cycles between timer assertions.</summary>
    public long TimerPeriodCycles => Math.Max(1, ClockHz / Hertz);

    /// <summary>Whether any enabled IRQ source is waiting to be acknowledged.</summary>
    public bool InterruptPending => (_irqMask & _irqPending) != 0;

    /// <summary>Whether any enabled NMI source is waiting to be acknowledged.</summary>
    public bool NmiPending => (_nmiMask & _nmiPending) != 0;

    /// <summary>IRQ mask written through E0h. Bit set enables the matching source.</summary>
    public byte IrqMask => _irqMask;

    /// <summary>NMI mask written through E4h. Bit set enables the matching source.</summary>
    public byte NmiMask => _nmiMask;

    /// <summary>Raw IRQ source latch.</summary>
    public byte IrqPendingSources => _irqPending;

    /// <summary>Raw NMI source latch.</summary>
    public byte NmiPendingSources => _nmiPending;

    /// <summary>Registers an action that presents a maskable interrupt to the CPU.</summary>
    public void SetInterruptCallback(Action? callback) => _interruptCallback = callback;

    /// <summary>Registers an action that presents NMI to the CPU.</summary>
    public void SetNmiCallback(Action? callback) => _nmiCallback = callback;

    /// <summary>Advances cycle-driven timer hardware.</summary>
    public void AdvanceCycles(long cycles)
    {
        if (cycles < 0)
            throw new ArgumentOutOfRangeException(nameof(cycles));

        _cycle += cycles;
        while (_cycle >= _timerDeadlineCycles)
        {
            AssertTimer();
            _timerDeadlineCycles += TimerPeriodCycles;
        }
    }

    /// <summary>Raises one timer interrupt when the IRQ mask enables it.</summary>
    public void Tick()
    {
        AssertTimer();
        _timerDeadlineCycles = _cycle + TimerPeriodCycles;
    }

    /// <summary>Raises or lowers the FDC INTRQ source.</summary>
    public void SetFdcInterruptRequest(bool active)
    {
        if (active)
            AssertNmi(NmiFdcIntrq);
        else
            _nmiPending &= unchecked((byte)~NmiFdcIntrq);
    }

    /// <summary>Raises reset NMI as an active-low E4h status bit.</summary>
    public void RequestResetNmi() => AssertNmi(NmiReset);

    /// <summary>Restores the reset state expected by the CP-500 interrupt hardware.</summary>
    public void Reset()
    {
        _irqMask = IrqTimer;
        _irqPending = 0;
        _nmiMask = 0;
        _nmiPending = 0;
        _cycle = 0;
        _timerDeadlineCycles = TimerPeriodCycles;
    }

    /// <summary>Registers E0h-E7h in both directions and the read side of ECh-EFh.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, FirstIrqPort, LastNmiPort);
        bus.RegisterInput(this, FirstTimerAcknowledgePort, LastTimerAcknowledgePort);
    }

    /// <summary>Returns active-low masked IRQ sources. Sources stay latched until acknowledged.</summary>
    public byte ReadIrqStatus() => (byte)~(_irqMask & _irqPending);

    /// <summary>Writes active-high IRQ source mask.</summary>
    public void WriteIrqMask(byte value) => _irqMask = value;

    /// <summary>Returns active-low masked NMI sources and clears the NMI source latch.</summary>
    public byte ReadNmiStatus()
    {
        byte value = (byte)~(_nmiMask & _nmiPending);
        _nmiPending = 0;
        return value;
    }

    /// <summary>Writes active-high NMI source mask.</summary>
    public void WriteNmiMask(byte value) => _nmiMask = value;

    public byte In(byte port)
    {
        switch (port)
        {
            case >= FirstIrqPort and <= LastIrqPort:
                return ReadIrqStatus();
            case >= FirstNmiPort and <= LastNmiPort:
                return ReadNmiStatus();
            case >= FirstTimerAcknowledgePort and <= LastTimerAcknowledgePort:
                _irqPending &= unchecked((byte)~IrqTimer);
                return 0x00;
            default:
                return 0xFF;
        }
    }

    public void Out(byte port, byte value)
    {
        switch (port)
        {
            case >= FirstIrqPort and <= LastIrqPort:
                WriteIrqMask(value);
                break;
            case >= FirstNmiPort and <= LastNmiPort:
                WriteNmiMask(value);
                break;
        }
    }

    private void AssertTimer()
    {
        if ((_irqMask & IrqTimer) == 0)
            return;

        _irqPending |= IrqTimer;
        _interruptCallback?.Invoke();
    }

    private void AssertNmi(byte source)
    {
        _nmiPending |= source;
        if ((_nmiMask & source) != 0)
            _nmiCallback?.Invoke();
    }
}
