using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>Cycle timing knobs for <see cref="Wd1791Controller"/>.</summary>
public sealed record Wd1791Timing(
    long CommandDelayCycles = 0,
    long ByteWindowCycles = 0,
    long RevolutionCycles = Wd1791Timing.DefaultRevolutionCycles,
    long IndexPulseCycles = Wd1791Timing.DefaultIndexPulseCycles)
{
    /// <summary>One revolution at 300 RPM on the 2.03 MHz CP-500 clock.</summary>
    public const long DefaultRevolutionCycles = 406_000;

    /// <summary>A pulse of about four milliseconds on the same clock.</summary>
    public const long DefaultIndexPulseCycles = 8_000;

    /// <summary>Compatibility timing that makes commands visible immediately.</summary>
    public static Wd1791Timing Immediate { get; } = new();
}

/// <summary>
/// WD1791-compatible floppy controller for CP-500 Model III geometry.
/// Commands can complete immediately for compatibility or after emulated-cycle
/// deadlines when a non-zero <see cref="Wd1791Timing"/> is supplied.
/// </summary>
public sealed class Wd1791Controller : IIoDevice
{
    /// <summary>Command/status register port.</summary>
    public const byte CommandStatusPort = 0xF0;

    /// <summary>Track register port.</summary>
    public const byte TrackPort = 0xF1;

    /// <summary>Sector register port.</summary>
    public const byte SectorPort = 0xF2;

    /// <summary>Data register port.</summary>
    public const byte DataPort = 0xF3;

    public const byte StatusBusy = 0x01;
    public const byte StatusDataRequest = 0x02;
    public const byte StatusIndex = 0x02;
    public const byte StatusLostData = 0x04;
    public const byte StatusTrackZero = 0x04;
    public const byte StatusCrcError = 0x08;
    public const byte StatusRecordNotFound = 0x10;
    public const byte StatusRecordType = 0x20;
    public const byte StatusHeadLoaded = 0x20;
    public const byte StatusWriteFault = 0x20;
    public const byte StatusWriteProtect = 0x40;
    public const byte StatusNotReady = 0x80;

    private const byte ForceInterruptCommand = 0xD;
    private const byte ForceInterruptOnIndex = 0x04;
    private const byte ForceInterruptImmediate = 0x08;
    private const byte HeadLoadFlag = 0x08;
    private const byte VerifyFlag = 0x04;
    private const byte UpdateTrackFlag = 0x10;
    private const byte MultiSectorFlag = 0x10;
    private const byte SideCompareEnableFlag = 0x02;
    private const byte SideCompareValueFlag = 0x08;
    private const byte DeletedDataMarkFlag = 0x01;
    private const byte SectorCommandMask = 0xC0;
    private const byte SectorCommandBits = 0x80;
    private const byte TypeIIOrIIIBits = 0x80;

    private readonly IModelIIIDiskImageProvider _images;
    private readonly Wd1791Timing _timing;
    private readonly int _installedDrives;
    private readonly int[] _headTrack = new int[ModelIIIDiskGeometry.MaxDrives];
    private byte[] _transferBuffer = [];
    private TransferMode _transferMode;
    private int _transferOffset;
    private bool _transferCrcError;
    private bool _stepDirectionIn = true;
    private bool _typeIStatus = true;
    private bool _headLoaded;
    private bool _multiSector;
    private bool _indexInterruptArmed;
    private ModelIIIDiskDensity _density = ModelIIIDiskDensity.DoubleDensity;
    private int _nextIdIndex;
    private byte _command;
    private byte _status;
    private long _cycle;
    private long _deadlineCycle;
    private long _drqDeadlineCycle;
    private bool _commandPending;

    /// <summary>
    /// Creates a controller that operates on the supplied storage provider.
    /// Only the first <paramref name="installedDrives"/> drives exist; the others never report TR00 or ready.
    /// </summary>
    public Wd1791Controller(
        IModelIIIDiskImageProvider images,
        Wd1791Timing? timing = null,
        int installedDrives = ModelIIIDiskGeometry.MaxDrives)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _timing = timing ?? Wd1791Timing.Immediate;
        if (installedDrives is < 0 or > ModelIIIDiskGeometry.MaxDrives)
            throw new ArgumentOutOfRangeException(nameof(installedDrives));

        _installedDrives = installedDrives;
    }

    /// <summary>Currently selected drive, controlled by the machine/front panel.</summary>
    public int SelectedDrive { get; private set; }

    /// <summary>Whether a drive-select line is active.</summary>
    public bool DriveSelected { get; private set; } = true;

    /// <summary>Currently selected side, controlled by the machine/front panel.</summary>
    public int SelectedSide { get; private set; }

    /// <summary>Recording density selected by the machine; sectors of the other density cannot be found.</summary>
    public ModelIIIDiskDensity Density => _density;

    /// <summary>WD1791 track register.</summary>
    public byte Track { get; private set; }

    /// <summary>Physical track under the head of the selected drive.</summary>
    public byte PhysicalTrack => (byte)_headTrack[SelectedDrive];

    /// <summary>WD1791 one-based sector register.</summary>
    public byte Sector { get; private set; } = 1;

    /// <summary>WD1791 data register.</summary>
    public byte Data { get; private set; }

    /// <summary>Whether the controller requests the next transfer byte.</summary>
    public bool DataRequest => (_status & StatusDataRequest) != 0;

    /// <summary>Whether the last command completed and awaits status acknowledgement.</summary>
    public bool InterruptRequest { get; private set; }

    /// <summary>Current WD1791 status bits, including the live drive signals of Type I commands.</summary>
    public byte Status => _typeIStatus ? (byte)(_status | DriveStatusBits()) : _status;

    /// <summary>Raised whenever DRQ changes.</summary>
    public event Action<bool>? DataRequestChanged;

    /// <summary>Raised whenever INTRQ changes.</summary>
    public event Action<bool>? InterruptRequestChanged;

    private bool DriveInstalled => DriveSelected && SelectedDrive < _installedDrives;

    private bool DriveReady => DriveInstalled && _images.IsMounted(SelectedDrive);

    private bool IsBusy => (_status & StatusBusy) != 0;

    private bool IndexPulseActive =>
        DriveReady && _timing.RevolutionCycles > 0 && _cycle % _timing.RevolutionCycles >= _timing.RevolutionCycles - _timing.IndexPulseCycles;

    private static bool IsMultiSectorCommand(byte command) =>
        (command & SectorCommandMask) == SectorCommandBits && (command & MultiSectorFlag) != 0;

    /// <summary>Registers the controller at the Model III F0h-F3h ports.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, CommandStatusPort, DataPort);
    }

    /// <summary>Advances emulated hardware time and fires any command/DRQ/index deadlines.</summary>
    public void AdvanceCycles(long cycles)
    {
        if (cycles < 0)
            throw new ArgumentOutOfRangeException(nameof(cycles));

        long previous = _cycle;
        _cycle += cycles;
        if (_commandPending && _cycle >= _deadlineCycle)
        {
            _commandPending = false;
            StartCommand();
        }

        if (DataRequest && _timing.ByteWindowCycles > 0 && _cycle >= _drqDeadlineCycle)
            SetError(StatusLostData);

        if (_indexInterruptArmed && DriveReady && IndexPulsesStartedBy(_cycle) > IndexPulsesStartedBy(previous))
            SetInterruptRequest(true);
    }

    /// <summary>Selects a conventional drive number.</summary>
    public void SelectDrive(int drive)
    {
        if (drive is < 0 or >= ModelIIIDiskGeometry.MaxDrives)
            throw new ArgumentOutOfRangeException(nameof(drive));

        SelectedDrive = drive;
        DriveSelected = true;
    }

    /// <summary>Disconnects every drive-select line from the controller.</summary>
    public void DeselectDrive() => DriveSelected = false;

    /// <summary>Selects head zero or one.</summary>
    public void SelectSide(int side)
    {
        if (side is < 0 or >= ModelIIIDiskGeometry.MaximumSides)
            throw new ArgumentOutOfRangeException(nameof(side));

        SelectedSide = side;
    }

    /// <summary>Selects FM or MFM recording for subsequent commands.</summary>
    public void SelectDensity(ModelIIIDiskDensity density) => _density = density;

    /// <summary>Restores reset-visible controller state, restoring every head to track zero without changing mounted media.</summary>
    public void Reset()
    {
        ClearTransfer();
        _status = 0;
        _typeIStatus = true;
        _headLoaded = false;
        _multiSector = false;
        _indexInterruptArmed = false;
        _density = ModelIIIDiskDensity.DoubleDensity;
        _nextIdIndex = 0;
        _command = 0;
        SetInterruptRequest(false);
        SelectedDrive = 0;
        DriveSelected = true;
        SelectedSide = 0;
        Array.Clear(_headTrack);
        Track = 0;
        Sector = 1;
        Data = 0;
        _stepDirectionIn = true;
        _cycle = 0;
        _deadlineCycle = 0;
        _drqDeadlineCycle = 0;
    }

    public byte In(byte port) => port switch
    {
        CommandStatusPort => ReadStatus(),
        TrackPort => Track,
        SectorPort => Sector,
        DataPort => ReadData(),
        _ => 0xFF
    };

    public void Out(byte port, byte value)
    {
        switch (port)
        {
            case CommandStatusPort:
                WriteCommand(value);
                break;
            case TrackPort:
                Track = value;
                break;
            case SectorPort:
                Sector = value;
                break;
            case DataPort:
                WriteData(value);
                break;
        }
    }

    private byte ReadStatus()
    {
        SetInterruptRequest(false);
        return Status;
    }

    private byte ReadData()
    {
        if (_transferMode != TransferMode.Read || !DataRequest)
            return Data;

        Data = _transferBuffer[_transferOffset++];
        ArmDrqDeadline();
        if (_transferOffset == _transferBuffer.Length)
            FinishRead();

        return Data;
    }

    private void WriteData(byte value)
    {
        Data = value;
        if (_transferMode is not (TransferMode.Write or TransferMode.WriteTrack) || !DataRequest)
            return;

        _transferBuffer[_transferOffset++] = value;
        ArmDrqDeadline();
        if (_transferOffset != _transferBuffer.Length)
            return;

        if (_transferMode == TransferMode.WriteTrack)
            FinishWriteTrack();
        else
            FinishWriteSector();
    }

    private void WriteCommand(byte command)
    {
        int type = command >> 4;
        if (type == ForceInterruptCommand)
        {
            ForceInterrupt(command);
            return;
        }

        if (IsBusy)
            return;

        _indexInterruptArmed = false;
        ClearTransfer();
        SetInterruptRequest(false);
        _status = 0;
        _command = command;
        _typeIStatus = command < TypeIIOrIIIBits;
        _multiSector = IsMultiSectorCommand(command);
        if (_typeIStatus)
            _headLoaded = (command & HeadLoadFlag) != 0;

        _status |= StatusBusy;
        if (_timing.CommandDelayCycles <= 0)
        {
            StartCommand();
            return;
        }

        _commandPending = true;
        _deadlineCycle = _cycle + _timing.CommandDelayCycles;
    }

    private void ForceInterrupt(byte command)
    {
        bool wasBusy = IsBusy;
        ClearTransfer();
        _multiSector = false;
        SetInterruptRequest(false);
        _status &= unchecked((byte)~StatusBusy);
        if (!wasBusy)
        {
            _typeIStatus = true;
            _status = 0;
        }

        _indexInterruptArmed = (command & ForceInterruptOnIndex) != 0;
        if ((command & ForceInterruptImmediate) != 0)
            SetInterruptRequest(true);
    }

    private void StartCommand()
    {
        switch (_command >> 4)
        {
            case 0x0:
                SetHeadTrack(0);
                Track = 0;
                FinishTypeI();
                break;
            case 0x1:
                SetHeadTrack(PhysicalTrack + (Data - Track));
                Track = Data;
                FinishTypeI();
                break;
            case 0x2:
            case 0x3:
                Step(_stepDirectionIn);
                break;
            case 0x4:
            case 0x5:
                Step(true);
                break;
            case 0x6:
            case 0x7:
                Step(false);
                break;
            case 0x8:
            case 0x9:
                BeginReadSector();
                break;
            case 0xA:
            case 0xB:
                BeginWriteSector();
                break;
            case 0xC:
                BeginReadAddress();
                break;
            case 0xE:
                BeginReadTrack();
                break;
            case 0xF:
                BeginWriteTrack();
                break;
        }
    }

    private void Step(bool inward)
    {
        SetHeadTrack(PhysicalTrack + (inward ? 1 : -1));
        if ((_command & UpdateTrackFlag) != 0)
            Track = (byte)(Track + (inward ? 1 : -1));

        FinishTypeI();
    }

    private void SetHeadTrack(int track)
    {
        if (track != PhysicalTrack)
            _stepDirectionIn = track > PhysicalTrack;

        if (DriveInstalled)
            _headTrack[SelectedDrive] = Math.Clamp(track, 0, ModelIIIDiskGeometry.Tracks - 1);
    }

    private void FinishTypeI()
    {
        bool verified = (_command & VerifyFlag) == 0 || TrackUnderHead().Any(sector => sector.RecordedTrack == Track);
        if (verified)
            CompleteCommand();
        else
            SetError(StatusRecordNotFound);
    }

    private void BeginReadSector()
    {
        if (!RequireDrive())
            return;

        if (FindSector() is not { } sector)
        {
            SetError(StatusRecordNotFound);
            return;
        }

        _status = (byte)((_status & ~StatusRecordType) | (sector.DeletedData ? StatusRecordType : 0));
        BeginTransfer(TransferMode.Read, sector.Data, crcError: !sector.CrcValid);
    }

    private void BeginWriteSector()
    {
        if (!RequireDrive())
            return;

        if (_images.IsReadOnly(SelectedDrive))
        {
            SetError(StatusWriteProtect);
            return;
        }

        if (FindSector() is not { } sector)
        {
            SetError(StatusRecordNotFound);
            return;
        }

        BeginTransfer(TransferMode.Write, new byte[sector.Data.Length]);
    }

    private void BeginReadAddress()
    {
        if (!RequireDrive())
            return;

        var candidates = SectorsOfSelectedDensity();
        if (candidates.Count == 0)
        {
            SetError(StatusRecordNotFound);
            return;
        }

        int index = _nextIdIndex % candidates.Count;
        _nextIdIndex = index + 1;
        var sector = candidates[index];
        byte[] id = [(byte)sector.RecordedTrack, (byte)sector.Side, (byte)sector.Id, sector.SizeCode];
        ushort crc = ModelIIIDiskCrc.ForField(_density, ModelIIITrackFormat.IdAddressMark, id);
        Sector = id[0];
        BeginTransfer(TransferMode.Read, [.. id, (byte)(crc >> 8), (byte)crc]);
    }

    private void BeginReadTrack()
    {
        if (!RequireDrive())
            return;

        byte[] track = new byte[ModelIIITrackFormat.TrackBytes(_density)];
        ModelIIITrackFormat.Encode(SectorsOfSelectedDensity(), track, _density, doubleFmBytes: false);
        BeginTransfer(TransferMode.Read, track);
    }

    private void BeginWriteTrack()
    {
        if (!RequireDrive())
            return;

        if (_images.IsReadOnly(SelectedDrive))
        {
            SetError(StatusWriteProtect);
            return;
        }

        BeginTransfer(TransferMode.WriteTrack, new byte[ModelIIITrackFormat.TrackBytes(_density)]);
    }

    private void FinishRead()
    {
        if (_transferCrcError)
        {
            SetError(StatusCrcError);
            return;
        }

        if (!_multiSector)
        {
            CompleteCommand();
            return;
        }

        SetDataRequest(false);
        Sector++;
        BeginReadSector();
    }

    private void FinishWriteSector()
    {
        bool deleted = (_command & DeletedDataMarkFlag) != 0;
        if (!_images.WriteSector(SelectedDrive, PhysicalTrack, SelectedSide, Sector, _transferBuffer, deleted))
        {
            SetError(WriteFailureStatus());
            return;
        }

        if (!_multiSector)
        {
            CompleteCommand();
            return;
        }

        SetDataRequest(false);
        Sector++;
        BeginWriteSector();
    }

    private void FinishWriteTrack()
    {
        var sectors = ModelIIITrackFormat.DecodeWriteTrack(_transferBuffer, _density, PhysicalTrack, SelectedSide);
        if (_images.ReplaceTrack(SelectedDrive, PhysicalTrack, SelectedSide, sectors))
            CompleteCommand();
        else
            SetError(WriteFailureStatus());
    }

    private byte WriteFailureStatus() => _images.IsReadOnly(SelectedDrive) ? StatusWriteProtect : StatusWriteFault;

    private IReadOnlyList<ModelIIIDiskSector> TrackUnderHead() =>
        DriveReady ? _images.GetTrack(SelectedDrive, PhysicalTrack, SelectedSide) : [];

    private List<ModelIIIDiskSector> SectorsOfSelectedDensity() =>
        TrackUnderHead().Where(sector => sector.Density == _density).ToList();

    private ModelIIIDiskSector? FindSector()
    {
        bool compareSide = (_command & SideCompareEnableFlag) != 0;
        int side = (_command & SideCompareValueFlag) != 0 ? 1 : 0;
        return SectorsOfSelectedDensity().FirstOrDefault(sector =>
            sector.Id == Sector && sector.RecordedTrack == Track && (!compareSide || sector.Side == side));
    }

    private bool RequireDrive()
    {
        if (DriveReady)
            return true;

        SetError(StatusNotReady);
        return false;
    }

    private byte DriveStatusBits()
    {
        byte bits = 0;
        if (IndexPulseActive) bits |= StatusIndex;
        if (DriveInstalled && PhysicalTrack == 0) bits |= StatusTrackZero;
        if (_headLoaded) bits |= StatusHeadLoaded;
        if (_images.IsReadOnly(SelectedDrive)) bits |= StatusWriteProtect;
        if (!DriveReady) bits |= StatusNotReady;
        return bits;
    }

    private long IndexPulsesStartedBy(long cycle) =>
        _timing.RevolutionCycles > 0 ? (cycle + _timing.IndexPulseCycles) / _timing.RevolutionCycles : 0;

    private void BeginTransfer(TransferMode mode, byte[] buffer, bool crcError = false)
    {
        _transferBuffer = buffer;
        _transferMode = mode;
        _transferOffset = 0;
        _transferCrcError = crcError;
        SetDataRequest(true);
    }

    private void CompleteCommand()
    {
        ClearTransfer();
        _status &= unchecked((byte)~StatusBusy);
        SetInterruptRequest(true);
    }

    private void SetError(byte statusBit)
    {
        ClearTransfer();
        _status |= statusBit;
        _status &= unchecked((byte)~StatusBusy);
        SetInterruptRequest(true);
    }

    private void ClearTransfer()
    {
        _commandPending = false;
        _transferMode = TransferMode.None;
        _transferOffset = 0;
        _transferBuffer = [];
        SetDataRequest(false);
    }

    private void SetDataRequest(bool value)
    {
        bool wasSet = DataRequest;
        if (value)
        {
            _status |= StatusDataRequest;
            ArmDrqDeadline();
        }
        else
        {
            _status &= unchecked((byte)~StatusDataRequest);
        }

        if (wasSet != value)
            DataRequestChanged?.Invoke(value);
    }

    private void SetInterruptRequest(bool value)
    {
        if (InterruptRequest == value)
            return;

        InterruptRequest = value;
        InterruptRequestChanged?.Invoke(value);
    }

    private void ArmDrqDeadline()
    {
        if (_timing.ByteWindowCycles > 0)
            _drqDeadlineCycle = _cycle + _timing.ByteWindowCycles;
    }

    private enum TransferMode
    {
        None,
        Read,
        Write,
        WriteTrack
    }
}
