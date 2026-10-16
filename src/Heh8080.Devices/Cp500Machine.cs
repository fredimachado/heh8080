using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Reusable CP-500 / TRS-80 Model III hardware composition.
/// It owns no UI, ROM file path, or disk filesystem; hosts supply those boundaries.
/// </summary>
public sealed class Cp500Machine
{
    /// <summary>Emulated time <see cref="ResetHoldingBreak"/> holds BREAK. The ROM needs at least half a second.</summary>
    public static readonly TimeSpan BreakHoldDuration = TimeSpan.FromSeconds(1);

    private readonly CycleThrottle _throttle;
    private readonly Cp500Rs232Device? _rs232;
    private readonly Cp500HardDiskController? _hardDisk;
    private long _breakCyclesRemaining;
    private double _speedFactor = 1;
    private bool _pacedForTyping;

    /// <summary>Builds a CP-500 machine from a user-supplied ROM image and pluggable host devices.</summary>
    public Cp500Machine(
        Cp500MachineProfile profile,
        ReadOnlySpan<byte> systemRom,
        IModelIIIDiskImageProvider? diskImages = null,
        IPrinterDevice? printer = null,
        Rs232Attachment serial = default,
        HardDiskAttachment hardDisk = default)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (serial.Installed != profile.Rs232Installed)
            throw new ArgumentException("RS-232 attachment does not match Rs232Installed.");
        if (!serial.Installed && serial.Endpoint is not null)
            throw new ArgumentException("An RS-232 endpoint requires Rs232Installed.");
        if (hardDisk.Installed != profile.HardDiskInstalled)
            throw new ArgumentException("Hard-disk attachment does not match HardDiskInstalled.");
        if (!hardDisk.Installed && (hardDisk.Media is not null || hardDisk.Clock is not null))
            throw new ArgumentException("A hard-disk slot or clock requires HardDiskInstalled.");
        if (hardDisk.Installed && (hardDisk.Media is null || hardDisk.Clock is null))
            throw new ArgumentException("An installed hard disk requires a slot and a clock.");
        if (systemRom.Length == 0)
            throw new ArgumentException("A CP-500 system ROM is required.", nameof(systemRom));

        Rom = new Cp500SystemRom(systemRom);
        Keyboard = new Cp500KeyboardDevice();
        Video = new Cp500VideoDevice();

        Memory = new MappedMemory(
            new KeyboardMatrixRegion(
                Cp500KeyboardDevice.StartAddress, Cp500KeyboardDevice.Length, Keyboard.Matrix, Keyboard.Automatic),
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
        if (profile.Rs232Installed)
        {
            _rs232 = new Cp500Rs232Device(profile.ClockHz, serial.Endpoint);
            _rs232.Register(IoBus);
            Interrupts.AttachRs232(_rs232);
            _rs232.StatusChanged = Interrupts.UpdateLines;
        }

        if (profile.HardDiskInstalled)
        {
            _hardDisk = new Cp500HardDiskController(hardDisk.Media!, hardDisk.Clock!);
            _hardDisk.Register(IoBus);
        }

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
    /// Multiple of real time that <see cref="LastSuggestedDelay"/> paces the whole machine at, timer and disk
    /// included, so software sees normal hardware. <see cref="double.PositiveInfinity"/> runs as fast as the host
    /// allows. While a host key is held the machine runs at real time instead: software measures key holds in
    /// emulated time, so a fast-forwarded keypress would last long enough to auto-repeat.
    /// Survives <see cref="Reset"/>. Not thread safe: change it only while <see cref="Step"/> is not running.
    /// </summary>
    public double SpeedFactor
    {
        get => _speedFactor;
        set
        {
            _throttle.SpeedFactor = value;
            _speedFactor = value;
            _pacedForTyping = false;
        }
    }

    /// <summary>
    /// Executes one Z80 instruction, advances the cycle-driven hardware (disk controller,
    /// 30 Hz timer, the RS-232 board, and the hard disk when each is installed) by that instruction's cycle count,
    /// and calculates the host delay needed for real-time pacing.
    /// </summary>
    public int Step()
    {
        Cpu.WaitLine = DiskControl.WaitActive;
        int cycles = Cpu.Step();
        DiskController.AdvanceCycles(cycles);
        Interrupts.AdvanceCycles(cycles);
        _rs232?.AdvanceCycles(cycles);
        _hardDisk?.AdvanceCycles(cycles);
        AdvanceBreakHold(cycles);
        Cpu.WaitLine = DiskControl.WaitActive;
        if (_speedFactor != 1)
            PaceTyping();
        LastSuggestedDelay = _throttle.GetDelay(cycles);
        return cycles;
    }

    private void PaceTyping()
    {
        bool typing = Keyboard.Matrix.AnyPressed;
        if (typing == _pacedForTyping)
            return;

        _pacedForTyping = typing;
        _throttle.SpeedFactor = typing ? 1 : _speedFactor;
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
        _rs232?.Reset();
        _hardDisk?.Reset();
        Video.ResetControl();
        _throttle.Reset();
        LastSuggestedDelay = TimeSpan.Zero;
        _breakCyclesRemaining = 0;
        Keyboard.SetAutomaticBreak(false);
    }

    /// <summary>
    /// Resets like <see cref="Reset"/> and holds BREAK for <see cref="BreakHoldDuration"/> of emulated time, counted
    /// down in <see cref="Step"/>. The ROM then skips the disk boot and offers BASIC, the Model III way to start
    /// without a boot disk. The hold is separate from host keys, which neither disturb nor release it.
    /// </summary>
    public void ResetHoldingBreak()
    {
        Reset();
        _breakCyclesRemaining = (long)(BreakHoldDuration.TotalSeconds * Profile.ClockHz);
        Keyboard.SetAutomaticBreak(true);
    }

    private void AdvanceBreakHold(int cycles)
    {
        if (_breakCyclesRemaining == 0)
            return;

        _breakCyclesRemaining = Math.Max(0, _breakCyclesRemaining - cycles);
        if (_breakCyclesRemaining == 0)
            Keyboard.SetAutomaticBreak(false);
    }
}
