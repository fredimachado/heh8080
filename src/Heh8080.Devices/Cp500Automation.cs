namespace Heh8080.Devices;

/// <summary>
/// Drives a <see cref="Cp500Machine"/> without a UI or real-time pacing, for command-line runners, tests and tool
/// servers: run for an emulated duration, run until text shows on screen, type, and read the screen and memory.
/// Time is counted only in executed cycles and <see cref="Cp500Machine.LastSuggestedDelay"/> is ignored, so a run
/// is deterministic and goes as fast as the host can execute it. Not thread safe.
/// </summary>
public sealed class Cp500Automation
{
    /// <summary>
    /// How long a typed key stays down. A 30 Hz scanner sees it on at least two consecutive scans (a debouncing
    /// keyboard driver needs that), and it is still short of the stock ROM's auto-repeat.
    /// </summary>
    public static readonly TimeSpan KeyHoldDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>How long every key stays up after a typed key, so the next press, even of the same key, is seen as new.</summary>
    public static readonly TimeSpan KeyGapDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>How often <see cref="RunUntilScreenContains"/> looks at the screen: once per 30 Hz frame.</summary>
    public static readonly TimeSpan ScreenCheckInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);

    private const int ShiftRow = 7;
    private const int ShiftColumn = 0;

    private long _scheduledCycles;

    /// <summary>
    /// Builds a machine around the ROM and the bay's disks and starts it the way the desktop does: when drives are
    /// installed and drive 0 is empty, it resets holding BREAK, so the stock ROM offers BASIC instead of waiting
    /// for a diskette.
    /// </summary>
    public Cp500Automation(
        ReadOnlySpan<byte> systemRom,
        Cp500DriveBay? drives = null,
        Cp500MachineProfile? profile = null,
        Rs232Attachment rs232 = default,
        HardDiskAttachment hardDisk = default,
        bool w5100 = false)
    {
        Drives = drives ?? new Cp500DriveBay();
        Machine = new Cp500Machine(
            profile ?? Cp500MachineProfile.Original,
            systemRom,
            Drives.Images,
            serial: rs232,
            hardDisk: hardDisk);
        if (w5100)
        {
            W5100 = new W5100Device();
            W5100.Register(Machine.IoBus);
        }

        if (Machine.Profile.InstalledDrives > 0 && !Drives.Images.IsMounted(0))
            Machine.ResetHoldingBreak();
    }

    /// <summary>The machine being driven.</summary>
    public Cp500Machine Machine { get; }

    /// <summary>The disks the machine reads; flush it to write modified disks back.</summary>
    public Cp500DriveBay Drives { get; }

    /// <summary>W5100 network board when the host attached one.</summary>
    public W5100Device? W5100 { get; }

    /// <summary>Z80 cycles executed since construction.</summary>
    public long ElapsedCycles { get; private set; }

    /// <summary>Emulated time since construction.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds((double)ElapsedCycles / Machine.Profile.ClockHz);

    /// <summary>Runs for an emulated duration.</summary>
    public void Run(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        AdvanceTo(_scheduledCycles + CyclesFor(duration));
    }

    /// <summary>
    /// Runs until the text appears anywhere within one screen row, checking before running and then every
    /// <see cref="ScreenCheckInterval"/>. Returns false when <paramref name="timeout"/> of emulated time passes first.
    /// Text already on screen satisfies the wait at once.
    /// </summary>
    public bool RunUntilScreenContains(string text, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        long deadline = _scheduledCycles + CyclesFor(timeout);
        long interval = CyclesFor(ScreenCheckInterval);

        while (!ScreenContains(text))
        {
            if (_scheduledCycles >= deadline)
                return false;

            AdvanceTo(Math.Min(deadline, _scheduledCycles + interval));
        }

        return true;
    }

    /// <summary>
    /// Types keys through the keyboard matrix: each key, with SHIFT when it asks for it, is held for
    /// <see cref="KeyHoldDuration"/> and followed by <see cref="KeyGapDuration"/> with every typed key up.
    /// </summary>
    public void Type(IReadOnlyList<Cp500KeyPress> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (Cp500KeyPress key in keys)
        {
            Machine.Keyboard.SetKeyState(ShiftRow, ShiftColumn, key.Shift);
            Machine.Keyboard.SetKeyState(key.Row, key.Column, pressed: true);
            Run(KeyHoldDuration);
            Machine.Keyboard.SetKeyState(key.Row, key.Column, pressed: false);
            Machine.Keyboard.SetKeyState(ShiftRow, ShiftColumn, pressed: false);
            Run(KeyGapDuration);
        }
    }

    /// <summary>Types text written in the <see cref="Cp500KeySequence"/> syntax.</summary>
    /// <exception cref="FormatException">The text cannot be typed on the CP-500 keyboard; nothing was typed.</exception>
    public void Type(string text) => Type(Cp500KeySequence.Parse(text));

    /// <summary>The visible screen rows, untrimmed, as <see cref="Cp500ScreenText.ReadRows"/> reads them.</summary>
    public IReadOnlyList<string> ReadScreenRows() => Cp500ScreenText.ReadRows(Machine.Video);

    /// <summary>The screen as printable text: trailing spaces trimmed, rows joined by '\n'.</summary>
    public string ReadScreenText() => Cp500ScreenText.Format(ReadScreenRows());

    /// <summary>Reads memory as the CPU sees it, from <paramref name="address"/> up to at most FFFFh.</summary>
    public byte[] ReadMemory(ushort address, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 0x10000 - address);
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = Machine.Memory.Read((ushort)(address + i));

        return bytes;
    }

    private bool ScreenContains(string text)
    {
        foreach (string row in ReadScreenRows())
        {
            if (row.Contains(text, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs until the cycle count reaches <paramref name="target"/>. Targets come from the schedule, not the count,
    /// because an instruction usually ends a few cycles past the target and that overshoot must not add up.
    /// </summary>
    private void AdvanceTo(long target)
    {
        while (ElapsedCycles < target)
            ElapsedCycles += Machine.Step();

        _scheduledCycles = target;
    }

    private long CyclesFor(TimeSpan duration) =>
        (long)Math.Round(duration.TotalSeconds * Machine.Profile.ClockHz);
}
