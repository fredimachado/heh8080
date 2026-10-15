using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Reusable CP-500 / TRS-80 Model III hardware composition.
/// It owns no UI, ROM file path, or disk filesystem; hosts supply those boundaries.
/// </summary>
public sealed class Cp500Machine
{
    private readonly CycleThrottle _throttle;

    /// <summary>Builds a CP-500 machine from a user-supplied ROM image and pluggable host devices.</summary>
    public Cp500Machine(
        Cp500MachineProfile profile,
        ReadOnlySpan<byte> systemRom,
        IModelIIIDiskImageProvider? diskImages = null,
        IPrinterDevice? printer = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (systemRom.Length == 0)
            throw new ArgumentException("A CP-500 system ROM is required.", nameof(systemRom));

        Rom = new Cp500SystemRom(systemRom);
        Keyboard = new Cp500KeyboardDevice();
        Video = new Cp500VideoDevice();

        Memory = new MappedMemory(
            new KeyboardMatrixRegion(Cp500KeyboardDevice.StartAddress, Cp500KeyboardDevice.Length, Keyboard.Matrix),
            Video.VideoRam,
            Rom,
            new RamMemoryRegion(0x4000, profile.RamSizeBytes));
        IoBus = new IoBus();
        Cpu = new CpuZ80(Memory, IoBus);

        DiskController = new Wd1791Controller(
            diskImages ?? new MemoryModelIIIDiskImageProvider(), installedDrives: profile.InstalledDrives);
        DiskControl = new Cp500DiskControlDevice(DiskController);
        Interrupts = new Cp500InterruptDevice(profile.ClockHz);
        Printer = new Cp500PrinterPort(printer);
        Cassette = new Cp500CassettePort();

        Interrupts.Register(IoBus);
        Video.Register(IoBus);
        DiskController.Register(IoBus);
        Rom.Register(IoBus);
        DiskControl.Register(IoBus);
        Printer.Register(IoBus);
        Cassette.Register(IoBus);
        Interrupts.InterruptLineChanged += asserted => Cpu.SetInterruptLine(asserted);
        Interrupts.NonMaskableInterruptRaised += Cpu.NonMaskableInterrupt;
        DiskController.InterruptRequestChanged += Interrupts.SetFdcInterruptRequest;

        _throttle = new CycleThrottle(Profile.ClockHz);
        Reset();
    }

    /// <summary>Machine hardware profile.</summary>
    public Cp500MachineProfile Profile { get; }

    /// <summary>CP-500 mapped address space.</summary>
    public MappedMemory Memory { get; }

    /// <summary>CP-500 Z80 processor.</summary>
    public CpuZ80 Cpu { get; }

    /// <summary>CP-500 I/O bus.</summary>
    public IoBus IoBus { get; }

    /// <summary>System EPROM and its F4h-F7h A11 flip-flop, overlaid by keyboard and video ranges.</summary>
    public Cp500SystemRom Rom { get; }

    /// <summary>Host-facing keyboard matrix.</summary>
    public Cp500KeyboardDevice Keyboard { get; }

    /// <summary>Host-facing 64-by-16 video plane.</summary>
    public Cp500VideoDevice Video { get; }

    /// <summary>IRQ and NMI masks, status, and the 30 Hz timer.</summary>
    public Cp500InterruptDevice Interrupts { get; }

    /// <summary>Cassette port stub at FFh.</summary>
    public Cp500CassettePort Cassette { get; }

    /// <summary>WD1791 disk controller.</summary>
    public Wd1791Controller DiskController { get; }

    /// <summary>Model III F4h-F7h disk drive/head/WAIT control latch.</summary>
    public Cp500DiskControlDevice DiskControl { get; }

    /// <summary>Parallel printer port.</summary>
    public Cp500PrinterPort Printer { get; }

    /// <summary>Delay suggested after the most recent <see cref="Step"/>.</summary>
    public TimeSpan LastSuggestedDelay { get; private set; }

    /// <summary>
    /// Executes one Z80 instruction, advances the cycle-driven hardware (disk controller and
    /// 30 Hz timer) by its duration, and calculates the host delay needed for real-time pacing.
    /// </summary>
    public int Step()
    {
        Cpu.WaitLine = DiskControl.WaitActive;
        int cycles = Cpu.Step();
        DiskController.AdvanceCycles(cycles);
        Interrupts.AdvanceCycles(cycles);
        Cpu.WaitLine = DiskControl.WaitActive;
        LastSuggestedDelay = _throttle.GetDelay(cycles);
        return cycles;
    }

    /// <summary>Resets CPU-visible hardware without clearing RAM or user-supplied media.</summary>
    public void Reset()
    {
        Rom.Reset();
        Cpu.Reset();
        Cpu.InterruptMode = 1;
        DiskController.Reset();
        DiskControl.Reset();
        Interrupts.Reset();
        Video.ResetControl();
        _throttle.Reset();
        LastSuggestedDelay = TimeSpan.Zero;
    }
}
