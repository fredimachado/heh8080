using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>Cycle delay for <see cref="Cp500HardDiskController"/>. The stock machine uses <see cref="Immediate"/>.</summary>
public sealed record HardDiskTiming
{
    /// <summary>Rejects a negative delay. Zero commits inside the command write.</summary>
    public HardDiskTiming(long commandDelayCycles = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(commandDelayCycles);
        CommandDelayCycles = commandDelayCycles;
    }

    /// <summary>Cycles from the command write until <see cref="Cp500HardDiskController.AdvanceCycles"/> may commit it.</summary>
    public long CommandDelayCycles { get; }

    /// <summary>Commands finish inside the OUT that starts them.</summary>
    public static HardDiskTiming Immediate { get; } = new();
}

/// <summary>
/// FreHD task file at C8h-CFh and the extra registers at C0h-C7h, including clock commands 01h and 02h.
/// One device owns the whole block so busy has one meaning. The machine registers it only when the flag is on.
/// </summary>
public sealed class Cp500HardDiskController : IIoDevice
{
    /// <summary>First port of the block.</summary>
    public const byte BasePort = 0xC0;

    /// <summary>Clock bytes and the extra data register.</summary>
    public const byte ExtraDataPort = 0xC2;

    /// <summary>Boot ROM stream on IN, extra command on OUT.</summary>
    public const byte ExtraCommandPort = 0xC4;

    /// <summary>Sector buffer.</summary>
    public const byte DataPort = 0xC8;

    /// <summary>Status on IN, command on OUT.</summary>
    public const byte StatusCommandPort = 0xCF;

    /// <summary>Command still running.</summary>
    public const byte StatusBusy = 0x80;

    /// <summary>Drive is ready. Set from reset onward.</summary>
    public const byte StatusReady = 0x40;

    /// <summary>Write fault. This phase never sets it.</summary>
    public const byte StatusWriteFault = 0x20;

    /// <summary>Seek complete latch.</summary>
    public const byte StatusSeekComplete = 0x10;

    /// <summary>The sector buffer can be moved. Busy is clear while this is set.</summary>
    public const byte StatusDataRequest = 0x08;

    /// <summary>Corrected data. This phase never sets it.</summary>
    public const byte StatusCorrected = 0x04;

    /// <summary>Set together with <see cref="StatusBusy"/>.</summary>
    public const byte StatusCommandInProgress = 0x02;

    /// <summary>The task-file error register is nonzero.</summary>
    public const byte StatusError = 0x01;

    /// <summary>Uncorrectable ECC. This phase never sets it.</summary>
    public const byte ErrorUncorrectable = 0x40;

    /// <summary>The sector is not on the selected drive.</summary>
    public const byte ErrorIdNotFound = 0x10;

    /// <summary>The command was rejected.</summary>
    public const byte ErrorAborted = 0x04;

    /// <summary>C0h bit 7, drive 0.</summary>
    public const byte ProtectDrive0 = 0x80;

    /// <summary>C0h bit 1, set with <see cref="ProtectDrive0"/> when that image is protected.</summary>
    public const byte ProtectAny = 0x02;

    private const byte SoftwareReset = 0x10;
    private const byte MultiSector = 0x04;
    private const int SectorBytes = HardDiskGeometry.BytesPerSector;
    private const int ClockBytes = 6;

    private readonly HardDiskSlot _slot;
    private readonly IClock _clock;
    private readonly HardDiskTiming _timing;
    private readonly byte[] _buffer = new byte[SectorBytes];

    private Phase _phase = new Phase.Idle();
    private long _cycle;
    private byte _command;
    private bool _extraAbort;
    private byte _control;
    private byte _extraData;
    private byte _extraSize;
    private byte _extraError;
    private byte _error;
    private byte _sectorCount;
    private byte _sector;
    private int _cylinder;
    private byte _size;
    private byte _drive;
    private byte _head;
    private bool _seekComplete;

    // The guest can write both. This phase has no boot ROM, and precompensation does not change a transfer.
#pragma warning disable CS0414
    private byte _precomp;
    private byte _bootRomAddress;
#pragma warning restore CS0414

    /// <summary>Creates the board around the bay slot. A null timing commits every command inside its OUT.</summary>
    public Cp500HardDiskController(HardDiskSlot slot, IClock clock, HardDiskTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(clock);
        _slot = slot;
        _clock = clock;
        _timing = timing ?? HardDiskTiming.Immediate;
        Reset();
    }

    /// <summary>Claims C0h-CFh.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, BasePort, StatusCommandPort);
    }

    /// <summary>Clears the task file and the phase. The mounted image and the clock stay.</summary>
    public void Reset()
    {
        ResetTaskFile();
        _control = 0;
        _extraData = 0;
        _extraSize = 0;
        _extraError = 0;
        _bootRomAddress = 0;
        _cycle = 0;
    }

    /// <summary>Counts CPU cycles. A busy command commits once the delay has elapsed.</summary>
    public void AdvanceCycles(long cycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycles);
        _cycle += cycles;
        if (_phase is Phase.Busy busy && _cycle >= busy.ReadyAtCycle)
            Commit(busy.Command);
    }

    /// <summary>Reads one register. Every port returns status while a command is busy.</summary>
    public byte In(byte port)
    {
        if (_phase is Phase.Busy)
            return ReadStatus();

        return port switch
        {
            BasePort => ProtectBits(),
            0xC1 => _control,
            ExtraDataPort => ReadExtraData(),
            0xC3 => _extraSize,
            ExtraCommandPort => 0xFF,
            0xC5 => _extraError,
            0xC6 or 0xC7 => 0xFF,
            DataPort => ReadSectorByte(),
            0xC9 => _error,
            0xCA => _sectorCount,
            0xCB => _sector,
            0xCC => (byte)_cylinder,
            0xCD => (byte)(_cylinder >> 8),
            0xCE => PackSdh(),
            StatusCommandPort => ReadStatus(),
            _ => 0xFF,
        };
    }

    /// <summary>Writes one register. Every write is ignored while a command is busy.</summary>
    public void Out(byte port, byte value)
    {
        if (_phase is Phase.Busy)
            return;

        switch (port)
        {
            case BasePort:
            case 0xC6:
            case 0xC7:
                break;
            case 0xC1:
                _control = value;
                if ((value & SoftwareReset) != 0)
                    ResetTaskFile();
                break;
            case ExtraDataPort:
                WriteExtraData(value);
                break;
            case 0xC3:
                _extraSize = value;
                break;
            case ExtraCommandPort:
                StartExtra(value);
                break;
            case 0xC5:
                _bootRomAddress = value;
                break;
            case DataPort:
                StoreSectorByte(value);
                break;
            case 0xC9:
                _precomp = value;
                break;
            case 0xCA:
                _sectorCount = value;
                break;
            case 0xCB:
                _sector = value;
                break;
            case 0xCC:
                _cylinder = (_cylinder & 0x300) | value;
                break;
            case 0xCD:
                _cylinder = ((value & 0x03) << 8) | (_cylinder & 0xFF);
                break;
            case 0xCE:
                _size = (byte)((value >> 5) & 0x03);
                _drive = (byte)((value >> 3) & 0x03);
                _head = (byte)(value & 0x07);
                _seekComplete = true;
                break;
            case StatusCommandPort:
                _command = value;
                Issue(TaskCommand(value), extra: false);
                break;
        }
    }

    private void ResetTaskFile()
    {
        _phase = new Phase.Idle();
        _error = 0;
        _sectorCount = 0;
        _sector = 0;
        _cylinder = 0;
        _size = 0;
        _drive = 0;
        _head = 0;
        _seekComplete = false;
        _precomp = 0;
        _command = 0;
        Array.Clear(_buffer);
    }

    private void Issue(Pending command, bool extra)
    {
        _extraAbort = extra;
        _phase = new Phase.Busy(command, _cycle + _timing.CommandDelayCycles);
        if (_timing.CommandDelayCycles == 0)
            Commit(command);
    }

    private void StartExtra(byte value)
    {
        switch (value & 0x0F)
        {
            case 0:
                return;
            case 1:
                Issue(Pending.ReadClock, extra: false);
                return;
            case 2:
                Issue(Pending.WriteClock, extra: false);
                return;
            default:
                Issue(Pending.Abort, extra: true);
                return;
        }
    }

    private static Pending TaskCommand(byte value) => (value >> 4) switch
    {
        0x1 => Pending.Restore,
        0x2 => Pending.ReadSector,
        0x3 => Pending.WriteSector,
        0x4 => Pending.Verify,
        0x5 => Pending.FormatTrack,
        0x6 => Pending.Init,
        0x7 => Pending.Seek,
        0x9 => Pending.Test,
        _ => Pending.Abort,
    };

    /// <summary>Runs the command that just left Busy. Delay 0 calls this from <see cref="Out"/>.</summary>
    private void Commit(Pending command)
    {
        switch (command)
        {
            case Pending.Restore:
                Restore();
                break;
            case Pending.ReadSector:
                BeginRead();
                break;
            case Pending.WriteSector:
                BeginWrite();
                break;
            case Pending.Verify:
                BeginVerify();
                break;
            case Pending.FormatTrack:
            case Pending.Init:
            case Pending.Test:
                Succeed();
                break;
            case Pending.Seek:
                Seek();
                break;
            case Pending.ReadClock:
                BeginClockRead();
                break;
            case Pending.WriteClock:
                BeginClockWrite();
                break;
            case Pending.Abort:
                Abort();
                break;
            default:
                throw Unexpected(command);
        }
    }

    private void Restore()
    {
        _cylinder = 0;
        _seekComplete = true;
        Succeed();
    }

    private void Seek()
    {
        _seekComplete = true;
        Succeed();
    }

    private void BeginRead()
    {
        if (RejectSize() || !LoadSector())
            return;

        _phase = new Phase.SectorRead(0);
    }

    private void BeginWrite()
    {
        if (RejectSize() || RejectMultiSector())
            return;

        if (SelectedImage() is not { } image)
        {
            Fail(ErrorIdNotFound);
            return;
        }

        // Protected media fails before the sector number is consulted.
        if (image.WriteProtected)
        {
            Fail(ErrorAborted);
            return;
        }

        _error = 0;
        _phase = new Phase.SectorWrite(0);
    }

    private void BeginVerify()
    {
        if (RejectSize())
            return;

        if (SelectedImage() is not { } image || !image.Geometry.Contains(_cylinder, _head, _sector))
        {
            Fail(ErrorIdNotFound);
            return;
        }

        Succeed();
    }

    private void BeginClockRead()
    {
        HardDiskTime time = _clock.Read();
        _extraError = 0;
        _phase = new Phase.ClockRead(time, 0);
    }

    private void BeginClockWrite()
    {
        _extraError = 0;
        _phase = new Phase.ClockWrite(new byte[ClockBytes], 0);
    }

    private void Abort()
    {
        if (_extraAbort)
            _extraError = ErrorAborted;
        else
            _error = ErrorAborted;

        _phase = new Phase.Idle();
    }

    private void Succeed()
    {
        _error = 0;
        _phase = new Phase.Idle();
    }

    private bool RejectSize()
    {
        if (_size == 0)
            return false;

        Fail(ErrorAborted);
        return true;
    }

    private bool RejectMultiSector()
    {
        if ((_command & MultiSector) == 0)
            return false;

        Fail(ErrorAborted);
        return true;
    }

    private bool LoadSector()
    {
        if (SelectedImage() is not { } image
            || image.ReadSector(_cylinder, _head, _sector, _buffer) != SectorResult.Ok)
        {
            Fail(ErrorIdNotFound);
            return false;
        }

        _error = 0;
        return true;
    }

    private void Fail(byte error)
    {
        _error = error;
        _phase = new Phase.Idle();
    }

    private HardDiskImage? SelectedImage() => _drive == 0 ? _slot.Image : null;

    private byte ReadSectorByte()
    {
        if (_phase is not Phase.SectorRead read)
            return 0;

        byte value = _buffer[read.Index];
        _phase = read.Index == SectorBytes - 1
            ? new Phase.Idle()
            : read with { Index = read.Index + 1 };
        return value;
    }

    private void StoreSectorByte(byte value)
    {
        if (_phase is not Phase.SectorWrite write)
            return;

        _buffer[write.Index] = value;
        if (write.Index < SectorBytes - 1)
        {
            _phase = write with { Index = write.Index + 1 };
            return;
        }

        HardDiskImage? image = SelectedImage();
        _error = image is null ? ErrorIdNotFound : ErrorFor(image.WriteSector(_cylinder, _head, _sector, _buffer));
        _phase = new Phase.Idle();
    }

    private static byte ErrorFor(SectorResult result) => result switch
    {
        SectorResult.Ok => 0,
        SectorResult.NotFound => ErrorIdNotFound,
        SectorResult.WriteProtected => ErrorAborted,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };

    private byte ReadExtraData()
    {
        if (_phase is not Phase.ClockRead clock)
            return _extraData;

        byte value = ClockByte(clock.Time, clock.Index);
        _phase = clock.Index == ClockBytes - 1
            ? new Phase.Idle()
            : clock with { Index = clock.Index + 1 };
        return value;
    }

    private void WriteExtraData(byte value)
    {
        if (_phase is not Phase.ClockWrite clock)
        {
            _extraData = value;
            return;
        }

        clock.Collected[clock.Index] = value;
        if (clock.Index < ClockBytes - 1)
        {
            _phase = clock with { Index = clock.Index + 1 };
            return;
        }

        if (TryDecodeClock(clock.Collected, out HardDiskTime time))
        {
            _clock.Set(time);
            _extraError = 0;
        }
        else
        {
            _extraError = ErrorAborted;
        }

        _phase = new Phase.Idle();
    }

    private static bool TryDecodeClock(byte[] bytes, out HardDiskTime time)
    {
        time = default;
        if (bytes[0] > 59 || bytes[1] > 59 || bytes[2] > 23 || bytes[4] is < 1 or > 31 || bytes[5] is < 1 or > 12)
            return false;

        time = new HardDiskTime(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5]);
        return time.TryToLocalDateTime(out _);
    }

    private static byte ClockByte(HardDiskTime time, int index) => index switch
    {
        0 => time.Seconds,
        1 => time.Minutes,
        2 => time.Hours,
        3 => time.YearSince2000,
        4 => time.Day,
        5 => time.Month,
        _ => 0,
    };

    private byte ProtectBits() =>
        _slot.Image is { WriteProtected: true } ? (byte)(ProtectDrive0 | ProtectAny) : (byte)0;

    private byte PackSdh() => (byte)((_size << 5) | (_drive << 3) | _head);

    private byte ReadStatus()
    {
        byte status = StatusReady;
        if (_seekComplete)
            status |= StatusSeekComplete;

        if (_error != 0)
            status |= StatusError;

        if (_phase is Phase.Busy)
            status |= StatusBusy | StatusCommandInProgress;

        // The buffer phase reports DRQ with busy clear. The guest polls busy, then moves 256 bytes.
        if (_phase is Phase.SectorRead or Phase.SectorWrite)
            status |= StatusDataRequest;

        return status;
    }

    private static InvalidOperationException Unexpected(Pending command) =>
        new(command switch
        {
            Pending.Restore => "restore",
            Pending.ReadSector => "read",
            Pending.WriteSector => "write",
            Pending.Verify => "verify",
            Pending.FormatTrack => "format",
            Pending.Init => "init",
            Pending.Seek => "seek",
            Pending.Test => "test",
            Pending.ReadClock => "read clock",
            Pending.WriteClock => "write clock",
            Pending.Abort => "abort",
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        });

    private abstract record Phase
    {
        private Phase()
        {
        }

        internal sealed record Idle : Phase;

        internal sealed record Busy(Pending Command, long ReadyAtCycle) : Phase;

        internal sealed record SectorRead(int Index) : Phase;

        internal sealed record SectorWrite(int Index) : Phase;

        internal sealed record ClockRead(HardDiskTime Time, int Index) : Phase;

        internal sealed record ClockWrite(byte[] Collected, int Index) : Phase;
    }

    private enum Pending
    {
        Restore,
        ReadSector,
        WriteSector,
        Verify,
        FormatTrack,
        Init,
        Seek,
        Test,
        ReadClock,
        WriteClock,
        Abort,
    }
}
