using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Reusable CP-500 / TRS-80 Model III hardware composition.
/// It owns no UI, ROM file path, or disk filesystem; hosts supply those boundaries.
/// </summary>
public sealed class Cp500Machine
{
    /// <summary>Builds a CP-500 machine from a user-supplied ROM image and pluggable host devices.</summary>
    public Cp500Machine(
        Cp500MachineProfile profile,
        ReadOnlySpan<byte> systemRom,
        IModelIIIDiskImageProvider? diskImages = null,
        IPrinterDevice? printer = null,
        ICp500Rs232Endpoint? rs232 = null,
        TimeProvider? timeProvider = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (systemRom.Length == 0)
            throw new ArgumentException("A CP-500 system ROM is required.", nameof(systemRom));

        byte[] romImage = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        Array.Fill(romImage, (byte)0xFF);
        systemRom[..Math.Min(systemRom.Length, romImage.Length)].CopyTo(romImage);

        Rom = new RomMemoryRegion(0x0000, romImage);
        Keyboard = new Cp500KeyboardDevice();
        KeyboardRegion = new KeyboardMatrixRegion(
            Cp500KeyboardDevice.StartAddress,
            Cp500KeyboardDevice.Length,
            Keyboard.Matrix);
        Video = new Cp500VideoDevice();
        Rtc = new Cp500RtcDevice(timeProvider);
        Ram = CreateRam(profile);
        Unmapped = new UnmappedMemoryRegion(0x0000, 65536);

        Memory = new MappedMemory(
            KeyboardRegion,
            Video.VideoRam,
            Rtc,
            Rom,
            Ram,
            Unmapped);
        IoBus = new IoBus();
        Cpu = new CpuZ80(Memory, IoBus);

        DiskImages = diskImages ?? new MemoryModelIIIDiskImageProvider();
        DiskController = new Wd1791Controller(DiskImages);
        DiskControl = new Cp500DiskControlDevice(DiskController);
        Interrupts = new Cp500InterruptDevice();
        Printer = new Cp500PrinterPort(printer);
        Rs232 = new Cp500Rs232Device(rs232);

        DiskController.Register(IoBus);
        DiskControl.Register(IoBus);
        Interrupts.Register(IoBus);
        Printer.Register(IoBus);
        Rs232.Register(IoBus);
        Interrupts.SetInterruptCallback(() => Cpu.Interrupt(0xFF));

        Throttle = new CycleThrottle(Profile.ClockHz);
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

    /// <summary>Read-only system ROM, overlaid by keyboard and video ranges.</summary>
    public RomMemoryRegion Rom { get; }

    /// <summary>Installed RAM region. A 64 KiB M80/M80C profile uses full-address-space underlay RAM.</summary>
    public RamMemoryRegion Ram { get; }

    /// <summary>Fallback open-bus region.</summary>
    public UnmappedMemoryRegion Unmapped { get; }

    /// <summary>Memory-mapped keyboard region.</summary>
    public KeyboardMatrixRegion KeyboardRegion { get; }

    /// <summary>Host-facing keyboard matrix.</summary>
    public Cp500KeyboardDevice Keyboard { get; }

    /// <summary>Host-facing 64-by-16 video plane.</summary>
    public Cp500VideoDevice Video { get; }

    /// <summary>Memory-mapped real-time clock.</summary>
    public Cp500RtcDevice Rtc { get; }

    /// <summary>Heartbeat interrupt latch.</summary>
    public Cp500InterruptDevice Interrupts { get; }

    /// <summary>Model III disk-image storage.</summary>
    public IModelIIIDiskImageProvider DiskImages { get; }

    /// <summary>WD1791 disk controller.</summary>
    public Wd1791Controller DiskController { get; }

    /// <summary>Model III F4h-F7h disk drive/head/WAIT control latch.</summary>
    public Cp500DiskControlDevice DiskControl { get; }

    /// <summary>Parallel printer port.</summary>
    public Cp500PrinterPort Printer { get; }

    /// <summary>Optional serial-adapter stub.</summary>
    public Cp500Rs232Device Rs232 { get; }

    /// <summary>Cycle-to-host-delay converter configured from the profile clock.</summary>
    public CycleThrottle Throttle { get; }

    /// <summary>Delay suggested after the most recent <see cref="Step"/>.</summary>
    public TimeSpan LastSuggestedDelay { get; private set; }

    /// <summary>Executes one Z80 instruction and calculates the host delay needed for real-time pacing.</summary>
    public int Step()
    {
        int cycles = Cpu.Step();
        LastSuggestedDelay = Throttle.GetDelay(cycles);
        return cycles;
    }

    /// <summary>Executes and asynchronously paces one instruction without blocking browser or UI threads.</summary>
    public async ValueTask<int> StepAsync(CancellationToken cancellationToken = default)
    {
        int cycles = Step();
        if (LastSuggestedDelay > TimeSpan.Zero)
            await Task.Delay(LastSuggestedDelay, cancellationToken);

        return cycles;
    }

    /// <summary>Presents one external 30 Hz heartbeat to the machine.</summary>
    public void TickInterrupt() => Interrupts.Tick();

    /// <summary>Enables or disables the M80/M80C boot-ROM overlay.</summary>
    public void SetRomOverlay(bool enabled)
    {
        if (!Profile.SupportsRomOverlay && !enabled)
        {
            throw new InvalidOperationException(
                "Only a 64 KiB M80/M80C profile can expose low RAM.");
        }

        Rom.Enabled = enabled;
    }

    /// <summary>Resets CPU-visible hardware without clearing RAM or user-supplied media.</summary>
    public void Reset()
    {
        Rom.Enabled = true;
        Cpu.Reset();
        Cpu.InterruptMode = 1;
        DiskController.Reset();
        DiskControl.Reset();
        Interrupts.Reset();
        Throttle.Reset();
        LastSuggestedDelay = TimeSpan.Zero;
    }

    private static RamMemoryRegion CreateRam(Cp500MachineProfile profile)
    {
        if (profile.SupportsRomOverlay)
            return new RamMemoryRegion(0x0000, 65536);

        return new RamMemoryRegion(0x4000, profile.RamSizeBytes);
    }
}
