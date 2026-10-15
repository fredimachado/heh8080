using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// CP-500 / Model III interrupt fabric. E0h-E3h hold the IRQ mask and
/// active-low IRQ status, E4h-E7h the NMI mask and active-low NMI status,
/// and reads of ECh-EFh acknowledge the 30 Hz timer. Cassette sources are
/// accepted in the mask but never asserted.
/// </summary>
/// <remarks>
/// The IRQ line is a level: it follows <c>mask &amp; pending</c> and stays high
/// until the timer is acknowledged. NMI is raised as an edge whenever an enabled
/// source becomes active; FDC INTRQ is the only NMI source and its status bit
/// mirrors the live controller line.
/// </remarks>
public sealed class Cp500InterruptDevice : IIoDevice
{
    public const byte FirstIrqPort = 0xE0;
    public const byte LastIrqPort = 0xE3;
    public const byte FirstNmiPort = 0xE4;
    public const byte LastNmiPort = 0xE7;
    public const byte FirstTimerAcknowledgePort = 0xEC;
    public const byte LastTimerAcknowledgePort = 0xEF;

    public const byte IrqTimer = 0x04;
    public const byte NmiFdcIntrq = 0x80;

    /// <summary>Frequency of the timer interrupt.</summary>
    public const int TimerHertz = 30;

    private byte _irqMask;
    private byte _irqPending;
    private byte _nmiMask;
    private bool _fdcIntrq;
    private bool _irqLine;
    private bool _nmiLine;
    private long _cycle;
    private long _timerDeadlineCycles;

    /// <summary>Creates the interrupt fabric with a timer derived from the emulated CPU clock.</summary>
    public Cp500InterruptDevice(long clockHz)
    {
        if (clockHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(clockHz));

        TimerPeriodCycles = Math.Max(1, clockHz / TimerHertz);
        Reset();
    }

    /// <summary>Raised when the maskable interrupt request line changes level.</summary>
    public event Action<bool>? InterruptLineChanged;

    /// <summary>Raised when an enabled NMI source becomes active.</summary>
    public event Action? NonMaskableInterruptRaised;

    /// <summary>Cycles between timer assertions.</summary>
    public long TimerPeriodCycles { get; }

    /// <summary>Advances the cycle-driven 30 Hz timer.</summary>
    public void AdvanceCycles(long cycles)
    {
        if (cycles < 0)
            throw new ArgumentOutOfRangeException(nameof(cycles));

        _cycle += cycles;
        while (_cycle >= _timerDeadlineCycles)
        {
            _irqPending |= IrqTimer;
            _timerDeadlineCycles += TimerPeriodCycles;
        }

        UpdateLines();
    }

    /// <summary>Follows the FDC INTRQ line, which drives the NMI status bit.</summary>
    public void SetFdcInterruptRequest(bool active)
    {
        _fdcIntrq = active;
        UpdateLines();
    }

    /// <summary>Restores the reset state expected by the CP-500 interrupt hardware.</summary>
    public void Reset()
    {
        _irqMask = IrqTimer;
        _irqPending = 0;
        _nmiMask = 0;
        _fdcIntrq = false;
        _cycle = 0;
        _timerDeadlineCycles = TimerPeriodCycles;
        UpdateLines();
    }

    /// <summary>Registers E0h-E7h in both directions and the read side of ECh-EFh.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, FirstIrqPort, LastNmiPort);
        bus.RegisterInput(this, FirstTimerAcknowledgePort, LastTimerAcknowledgePort);
    }

    public byte In(byte port)
    {
        switch (port)
        {
            case >= FirstIrqPort and <= LastIrqPort:
                return (byte)~(_irqMask & _irqPending);
            case >= FirstNmiPort and <= LastNmiPort:
                return (byte)~(_nmiMask & NmiSources);
            case >= FirstTimerAcknowledgePort and <= LastTimerAcknowledgePort:
                _irqPending &= unchecked((byte)~IrqTimer);
                UpdateLines();
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
                _irqMask = value;
                break;
            case >= FirstNmiPort and <= LastNmiPort:
                _nmiMask = value;
                break;
        }

        UpdateLines();
    }

    private byte NmiSources => _fdcIntrq ? NmiFdcIntrq : (byte)0;

    private void UpdateLines()
    {
        bool irq = (_irqMask & _irqPending) != 0;
        if (irq != _irqLine)
        {
            _irqLine = irq;
            InterruptLineChanged?.Invoke(irq);
        }

        bool nmi = (_nmiMask & NmiSources) != 0;
        bool nmiRose = nmi && !_nmiLine;
        _nmiLine = nmi;
        if (nmiRose)
            NonMaskableInterruptRaised?.Invoke();
    }
}
