using System.Globalization;
using Heh8080.Devices;

namespace Heh8080.Cp500Host;

/// <summary>Emulated duration as Z80 cycles at the machine clock.</summary>
public readonly record struct EmulatedTime
{
    public long Cycles { get; }

    private EmulatedTime(long cycles) => Cycles = cycles;

    /// <summary>Rejects a non-positive length.</summary>
    public static EmulatedTime FromSeconds(double seconds, long clockHz)
    {
        if (clockHz <= 0 || seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            throw new Cp500Rejected("Emulated time must be positive.");

        double cycles = Math.Round(seconds * clockHz, MidpointRounding.AwayFromZero);
        if (cycles < 1 || cycles > long.MaxValue)
            throw new Cp500Rejected("Emulated time must be positive.");

        return new EmulatedTime((long)cycles);
    }

    /// <summary>Parses "30s" or "500ms". A missing unit, zero, or a negative value is rejected.</summary>
    public static EmulatedTime Parse(string text, long clockHz)
    {
        if (string.IsNullOrEmpty(text))
            throw new Cp500Rejected("Timeout needs a unit of s or ms.");

        int divisor;
        string number;
        if (text.EndsWith("ms", StringComparison.Ordinal))
        {
            divisor = 1000;
            number = text[..^2];
        }
        else if (text.EndsWith('s'))
        {
            divisor = 1;
            number = text[..^1];
        }
        else
            throw new Cp500Rejected("Timeout needs a unit of s or ms.");

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double magnitude)
            || magnitude <= 0
            || double.IsInfinity(magnitude))
            throw new Cp500Rejected("Timeout must be positive.");

        return FromSeconds(magnitude / divisor, clockHz);
    }
}

/// <summary>Non-empty ordinal screen snippet.</summary>
public readonly record struct ScreenText
{
    public string Value { get; }

    private ScreenText(string value) => Value = value;

    public static ScreenText Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            throw new Cp500Rejected("Screen text must not be empty.");

        return new ScreenText(text);
    }
}

/// <summary>One mounted image. Write-back is null when the host cannot save.</summary>
public sealed record Cp500Disk(byte[] Image, string Name, Action<byte[]>? WriteBack);

/// <summary>Bytes for the session to insert. Write-back is <see cref="File.WriteAllBytes"/> on this host. Read-only stays false.</summary>
public sealed record Cp500HardDisk(
    byte[] Image,
    string Name,
    Action<byte[]>? WriteBack,
    HardDiskGeometry? RawGeometry = null);

/// <summary>
/// Disks for one CP-500. An index outside 0 .. MaxDrives-1 is rejected.
/// Installed count is at least the original two, and rises when a higher drive is filled.
/// </summary>
public sealed class Cp500Media
{
    private readonly Cp500Disk?[] _disks = new Cp500Disk?[ModelIIIDiskGeometry.MaxDrives];

    public static void EnsureInstalled(int drive)
    {
        if (drive < 0 || drive >= ModelIIIDiskGeometry.MaxDrives)
            throw new Cp500Rejected(
                $"Drive {drive} is outside 0..{ModelIIIDiskGeometry.MaxDrives - 1}.");
    }

    /// <summary>
    /// max(original installed drives, highest filled index + 1).
    /// No disks, or only 0 and 1, stays at 2. Drive 2 installs 3. Drive 3 installs 4.
    /// </summary>
    public int InstalledDrives
    {
        get
        {
            int highest = -1;
            for (int drive = 0; drive < _disks.Length; drive++)
            {
                if (_disks[drive] is not null)
                    highest = drive;
            }

            return Math.Max(Cp500MachineProfile.Original.InstalledDrives, highest + 1);
        }
    }

    public void Set(int drive, Cp500Disk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        EnsureInstalled(drive);
        _disks[drive] = disk;
    }

    public Cp500Disk? Get(int drive)
    {
        EnsureInstalled(drive);
        return _disks[drive];
    }
}

/// <summary>
/// Sole owner of one CP-500 machine, its drive bay, and the keyboard matrix.
/// Public methods take one lock. Speed is infinite. Holds are cycle counts.
/// </summary>
public sealed class Cp500Session : IDisposable
{
    public const double KeyHoldSeconds = 0.1;
    public const double KeySettleSeconds = 0.5;
    public const double ScreenPollSeconds = 0.25;
    public const int MaxPeekLength = 256;
    public const int MaxInstructionsPerStep = 1_000_000;

    private readonly Lock _gate = new();
    private readonly Cp500Machine _machine;
    private readonly Cp500DriveBay _bay;
    private readonly ISerialEndpoint? _endpoint;
    private bool _disposed;

    private Cp500Session(
        ReadOnlyMemory<byte> rom,
        Cp500Media media,
        Rs232Attachment rs232,
        Cp500HardDisk? hardDisk,
        IClock? clock)
    {
        ArgumentNullException.ThrowIfNull(media);
        if ((hardDisk is null) != (clock is null))
            throw new ArgumentException("A hard disk and a clock are passed together.");

        if (rom.Length == 0 || rom.Length > Cp500MachineProfile.SystemRomSizeBytes)
            throw new Cp500Rejected(
                $"ROM must be 1 through {Cp500MachineProfile.SystemRomSizeBytes} bytes.");

        _endpoint = rs232.Endpoint;
        _bay = new Cp500DriveBay();
        HardDiskAttachment board = HardDiskAttachment.None;
        if (hardDisk is { } image && clock is { } time)
        {
            try
            {
                _bay.HardDisk.Insert(image.Image, image.Name, image.WriteBack, readOnly: false, image.RawGeometry);
            }
            catch (InvalidDataException ex)
            {
                throw new Cp500Rejected($"Hard disk '{image.Name}' is not a valid image: {ex.Message}");
            }

            board = HardDiskAttachment.Install(_bay.HardDisk, time);
        }

        var profile = board.Apply(rs232.Apply(new Cp500MachineProfile(
            Cp500MachineProfile.Original.RamSizeKiB,
            media.InstalledDrives)
        {
            ClockHz = Cp500MachineProfile.Original.ClockHz
        }));
        _machine = new Cp500Machine(profile, rom.Span, _bay.Images, serial: rs232, hardDisk: board);
        for (int drive = 0; drive < ModelIIIDiskGeometry.MaxDrives; drive++)
        {
            if (media.Get(drive) is not { } disk)
                continue;

            try
            {
                _bay.Insert(drive, disk.Image, disk.Name, disk.WriteBack);
            }
            catch (InvalidDataException ex)
            {
                throw new Cp500Rejected($"Disk '{disk.Name}' is not a valid image: {ex.Message}");
            }
        }

        _machine.SpeedFactor = double.PositiveInfinity;
    }

    /// <summary>
    /// Inserts disks, sets infinite speed, then <see cref="Boot"/>.
    /// <paramref name="hardDisk"/> and <paramref name="clock"/> are both null, or both set.
    /// The session inserts into its own bay slot, then <see cref="HardDiskAttachment.Apply"/>.
    /// </summary>
    public static Cp500Session Create(
        ReadOnlyMemory<byte> rom,
        Cp500Media media,
        Rs232Attachment rs232 = default,
        Cp500HardDisk? hardDisk = null,
        IClock? clock = null)
    {
        var session = new Cp500Session(rom, media, rs232, hardDisk, clock);
        session.Boot();
        return session;
    }

    public int InstalledDrives
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _machine.Profile.InstalledDrives;
            }
        }
    }

    public bool IsDriveMounted(int drive)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Cp500Media.EnsureInstalled(drive);
            return _bay.Images.IsMounted(drive);
        }
    }

    /// <summary>
    /// Releases host keys, then resets. Drive 0 mounted calls Reset.
    /// Drive 0 empty holds BREAK for one emulated second and steps it off before return.
    /// </summary>
    public void Boot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _machine.Keyboard.ReleaseAll();
            if (_bay.Images.IsMounted(0))
            {
                _machine.Reset();
                return;
            }

            _machine.ResetHoldingBreak();
            StepCycles(BreakHoldCycles());
        }
    }

    /// <summary>
    /// Presses every decoded key, or none. CR and backslash + r are ENTER.
    /// An unmapped character is rejected before the matrix changes.
    /// </summary>
    public void Type(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Cp500KeyStroke[] strokes = Decode(text);
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                foreach (Cp500KeyStroke stroke in strokes)
                    Press(stroke);
            }
            finally
            {
                _machine.Keyboard.ReleaseAll();
            }
        }
    }

    public string ReadScreen()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return ReadScreenUnlocked();
        }
    }

    public byte[] Peek(ushort address, int length)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (length is < 1 or > MaxPeekLength)
                throw new Cp500Rejected($"Peek length must be 1 through {MaxPeekLength}.");

            if ((uint)address + (uint)length > 0x10000)
                throw new Cp500Rejected("Peek runs past FFFFh.");

            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = _machine.Memory.Read((ushort)(address + i));

            return bytes;
        }
    }

    public void Poke(ushort address, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if ((uint)address + (uint)data.Length > 0x10000)
                throw new Cp500Rejected("Poke runs past FFFFh.");

            for (int i = 0; i < data.Length; i++)
                _machine.Memory.Write((ushort)(address + i), data[i]);
        }
    }

    public int Step(int instructions)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (instructions is < 1 or > MaxInstructionsPerStep)
                throw new Cp500Rejected($"Step count must be 1 through {MaxInstructionsPerStep}.");

            int cycles = 0;
            for (int i = 0; i < instructions; i++)
                cycles += StepOnce();

            return cycles;
        }
    }

    /// <summary>
    /// Steps until the screen contains <paramref name="text"/>, or the cycle budget is spent.
    /// Checks before the first step. Timeout returns false.
    /// </summary>
    public bool RunUntil(ScreenText text, EmulatedTime timeout)
    {
        if (string.IsNullOrEmpty(text.Value))
            throw new Cp500Rejected("Screen text must not be empty.");

        lock (_gate)
        {
            ThrowIfDisposed();
            long poll = EmulatedTime.FromSeconds(ScreenPollSeconds, _machine.Profile.ClockHz).Cycles;
            long spent = 0;
            while (true)
            {
                if (ReadScreenUnlocked().Contains(text.Value, StringComparison.Ordinal))
                    return true;

                if (spent >= timeout.Cycles)
                    return false;

                long slice = Math.Min(poll, timeout.Cycles - spent);
                spent += StepCycles(slice);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Cp500Session));

            _disposed = true;
            try
            {
                _bay.Flush();
            }
            catch (Exception ex)
            {
                _endpoint?.Dispose();
                throw new Cp500Rejected($"Disk flush failed: {ex.Message}");
            }

            _endpoint?.Dispose();
        }
    }

    private string ReadScreenUnlocked() => Cp500Screen.ToText(_machine.Video);

    private long BreakHoldCycles() =>
        (long)(Cp500Machine.BreakHoldDuration.TotalSeconds * _machine.Profile.ClockHz);

    private void Press(Cp500KeyStroke stroke)
    {
        Cp500KeyStroke shift = Cp500KeyMap.Map(Cp500HostKey.Shift);
        bool shifted = stroke.Shift == Cp500ShiftMode.Shifted;
        if (shifted)
            _machine.Keyboard.SetKeyState(shift.Row, shift.Column, pressed: true);

        _machine.Keyboard.SetKeyState(stroke.Row, stroke.Column, pressed: true);
        StepCycles(EmulatedTime.FromSeconds(KeyHoldSeconds, _machine.Profile.ClockHz).Cycles);
        _machine.Keyboard.SetKeyState(stroke.Row, stroke.Column, pressed: false);
        if (shifted)
            _machine.Keyboard.SetKeyState(shift.Row, shift.Column, pressed: false);

        StepCycles(EmulatedTime.FromSeconds(KeySettleSeconds, _machine.Profile.ClockHz).Cycles);
    }

    private static Cp500KeyStroke[] Decode(string text)
    {
        var strokes = new List<Cp500KeyStroke>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '\r')
            {
                strokes.Add(Cp500KeyMap.Map(Cp500HostKey.Enter));
                continue;
            }

            if (character == '\\' && i + 1 < text.Length && text[i + 1] == 'r')
            {
                strokes.Add(Cp500KeyMap.Map(Cp500HostKey.Enter));
                i++;
                continue;
            }

            if (!Cp500KeyMap.TryMapCharacter(character, out Cp500KeyStroke stroke))
                throw new Cp500Rejected($"No CP-500 key for '{character}'.");

            strokes.Add(stroke);
        }

        return [.. strokes];
    }

    private long StepCycles(long cycles)
    {
        long spent = 0;
        while (spent < cycles)
            spent += StepOnce();

        return spent;
    }

    /// <summary>The only call to <see cref="Cp500Machine.Step"/> in this host. Caller holds the session lock.</summary>
    private int StepOnce() => _machine.Step();

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Cp500Session));
    }
}
