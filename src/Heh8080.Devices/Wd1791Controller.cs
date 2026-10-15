using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>Cycle timing knobs for <see cref="Wd1791Controller"/>.</summary>
public sealed record Wd1791Timing(
    long CommandDelayCycles = 0,
    long ByteWindowCycles = 0,
    long MotorTimeoutCycles = 0)
{
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
    public const byte StatusLostData = 0x04;
    public const byte StatusCrcError = 0x08;
    public const byte StatusRecordNotFound = 0x10;
    public const byte StatusWriteProtect = 0x40;
    public const byte StatusNotReady = 0x80;

    private readonly IModelIIIDiskImageProvider _images;
    private readonly Wd1791Timing _timing;
    private readonly byte[] _defaultTransferBuffer = new byte[ModelIIIDiskGeometry.BytesPerSector];
    private byte[] _transferBuffer;
    private TransferMode _transferMode;
    private int _transferOffset;
    private bool _stepDirectionIn = true;
    private byte _status;
    private long _cycle;
    private long _deadlineCycle;
    private long _drqDeadlineCycle;
    private PendingCommand _pendingCommand;

    /// <summary>Creates a controller that operates on the supplied storage provider.</summary>
    public Wd1791Controller(IModelIIIDiskImageProvider images, Wd1791Timing? timing = null)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _timing = timing ?? Wd1791Timing.Immediate;
        _transferBuffer = _defaultTransferBuffer;
    }

    /// <summary>Currently selected drive, controlled by the machine/front panel.</summary>
    public int SelectedDrive { get; private set; }

    /// <summary>Whether a drive-select line is active.</summary>
    public bool DriveSelected { get; private set; } = true;

    /// <summary>Currently selected side, controlled by the machine/front panel.</summary>
    public int SelectedSide { get; private set; }

    /// <summary>WD1791 track register.</summary>
    public byte Track { get; private set; }

    /// <summary>Physical track under the selected head.</summary>
    public byte PhysicalTrack { get; private set; }

    /// <summary>WD1791 one-based sector register.</summary>
    public byte Sector { get; private set; } = 1;

    /// <summary>WD1791 data register.</summary>
    public byte Data { get; private set; }

    /// <summary>Whether the controller requests the next transfer byte.</summary>
    public bool DataRequest => (_status & StatusDataRequest) != 0;

    /// <summary>Whether the last command completed and awaits status acknowledgement.</summary>
    public bool InterruptRequest { get; private set; }

    /// <summary>Current WD1791 status bits.</summary>
    public byte Status => _status;

    /// <summary>Elapsed emulated cycles known to the controller.</summary>
    public long ElapsedCycles => _cycle;

    /// <summary>Raised whenever DRQ changes.</summary>
    public event Action<bool>? DataRequestChanged;

    /// <summary>Raised whenever INTRQ changes.</summary>
    public event Action<bool>? InterruptRequestChanged;

    /// <summary>Registers the controller at the Model III F0h-F3h ports.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, CommandStatusPort, DataPort);
    }

    /// <summary>Advances emulated hardware time and fires any command/DRQ deadlines.</summary>
    public void AdvanceCycles(long cycles)
    {
        if (cycles < 0)
            throw new ArgumentOutOfRangeException(nameof(cycles));

        _cycle += cycles;
        if (_pendingCommand != PendingCommand.None && _cycle >= _deadlineCycle)
        {
            var pending = _pendingCommand;
            _pendingCommand = PendingCommand.None;
            StartPendingCommand(pending);
        }

        if (DataRequest && _timing.ByteWindowCycles > 0 && _cycle >= _drqDeadlineCycle)
            SetError(StatusLostData);
    }

    /// <summary>Selects a conventional drive number.</summary>
    public void SelectDrive(int drive)
    {
        if (drive is < 0 or >= MemoryModelIIIDiskImageProvider.MaxDrives)
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

    /// <summary>Restores reset-visible controller state without changing mounted media.</summary>
    public void Reset()
    {
        ClearTransfer();
        _status = 0;
        SetInterruptRequest(false);
        SelectedDrive = 0;
        DriveSelected = true;
        SelectedSide = 0;
        Track = 0;
        PhysicalTrack = 0;
        Sector = 1;
        Data = 0;
        _stepDirectionIn = true;
        _cycle = 0;
        _deadlineCycle = 0;
        _drqDeadlineCycle = 0;
        _pendingCommand = PendingCommand.None;
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
                ExecuteCommand(value);
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
        return _status;
    }

    private byte ReadData()
    {
        if (_transferMode != TransferMode.Read || !DataRequest)
            return Data;

        Data = _transferBuffer[_transferOffset++];
        ArmDrqDeadline();
        if (_transferOffset == _transferBuffer.Length)
            CompleteCommand();

        return Data;
    }

    private void WriteData(byte value)
    {
        Data = value;
        if (_transferMode != TransferMode.Write && _transferMode != TransferMode.WriteTrack || !DataRequest)
            return;

        _transferBuffer[_transferOffset++] = value;
        ArmDrqDeadline();
        if (_transferOffset != _transferBuffer.Length)
            return;

        bool ok = _transferMode == TransferMode.WriteTrack
            ? _images.WriteTrack(SelectedDrive, PhysicalTrack, SelectedSide, _transferBuffer)
            : _images.WriteSector(SelectedDrive, PhysicalTrack, SelectedSide, Sector, _transferBuffer);
        if (!ok)
        {
            SetError(_images.IsReadOnly(SelectedDrive) ? StatusWriteProtect : StatusRecordNotFound);
            return;
        }

        CompleteCommand();
    }

    private void ExecuteCommand(byte command)
    {
        ClearTransfer();
        SetInterruptRequest(false);
        _status = 0;

        switch (command & 0xF0)
        {
            case 0x00:
                QueueOrStart(PendingCommand.Restore);
                break;
            case 0x10:
                QueueOrStart(PendingCommand.Seek);
                break;
            case 0x20:
            case 0x30:
                QueueOrStart((command & 0x10) != 0 ? PendingCommand.StepUpdate : PendingCommand.Step);
                break;
            case 0x40:
            case 0x50:
                _stepDirectionIn = true;
                QueueOrStart((command & 0x10) != 0 ? PendingCommand.StepInUpdate : PendingCommand.StepIn);
                break;
            case 0x60:
            case 0x70:
                _stepDirectionIn = false;
                QueueOrStart((command & 0x10) != 0 ? PendingCommand.StepOutUpdate : PendingCommand.StepOut);
                break;
            case 0x80:
            case 0x90:
                QueueOrStart(PendingCommand.ReadSector);
                break;
            case 0xA0:
            case 0xB0:
                QueueOrStart(PendingCommand.WriteSector);
                break;
            case 0xC0:
                QueueOrStart(PendingCommand.ReadAddress);
                break;
            case 0xD0:
                CompleteCommand();
                break;
            case 0xE0:
                QueueOrStart(PendingCommand.ReadTrack);
                break;
            case 0xF0:
                QueueOrStart(PendingCommand.WriteTrack);
                break;
            default:
                SetError(StatusRecordNotFound);
                break;
        }
    }

    private void QueueOrStart(PendingCommand command)
    {
        _status |= StatusBusy;
        if (_timing.CommandDelayCycles <= 0)
        {
            StartPendingCommand(command);
            return;
        }

        _pendingCommand = command;
        _deadlineCycle = _cycle + _timing.CommandDelayCycles;
    }

    private void StartPendingCommand(PendingCommand command)
    {
        switch (command)
        {
            case PendingCommand.Restore:
                PhysicalTrack = 0;
                Track = 0;
                CompleteCommand();
                break;
            case PendingCommand.Seek:
                PhysicalTrack = ClampTrack(Data);
                Track = PhysicalTrack;
                CompleteCommand();
                break;
            case PendingCommand.Step:
                Step(_stepDirectionIn, false);
                break;
            case PendingCommand.StepUpdate:
                Step(_stepDirectionIn, true);
                break;
            case PendingCommand.StepIn:
                Step(true, false);
                break;
            case PendingCommand.StepInUpdate:
                Step(true, true);
                break;
            case PendingCommand.StepOut:
                Step(false, false);
                break;
            case PendingCommand.StepOutUpdate:
                Step(false, true);
                break;
            case PendingCommand.ReadSector:
                BeginReadSector();
                break;
            case PendingCommand.WriteSector:
                BeginWriteSector();
                break;
            case PendingCommand.ReadAddress:
                BeginReadAddress();
                break;
            case PendingCommand.ReadTrack:
                BeginReadTrack();
                break;
            case PendingCommand.WriteTrack:
                BeginWriteTrack();
                break;
        }
    }

    private void Step(bool inward, bool updateTrackRegister)
    {
        if (inward)
        {
            if (PhysicalTrack < ModelIIIDiskGeometry.Tracks - 1)
                PhysicalTrack++;
        }
        else if (PhysicalTrack > 0)
        {
            PhysicalTrack--;
        }

        if (updateTrackRegister)
            Track = PhysicalTrack;

        CompleteCommand();
    }

    private void BeginReadSector()
    {
        if (!CheckMedia())
            return;

        if (!_images.ReadSector(SelectedDrive, PhysicalTrack, SelectedSide, Sector, _defaultTransferBuffer))
        {
            SetError(_images.TryGetSectorHeader(SelectedDrive, PhysicalTrack, SelectedSide, Sector, out var header) && !header.CrcValid
                ? StatusCrcError
                : StatusRecordNotFound);
            return;
        }

        _transferBuffer = _defaultTransferBuffer;
        _transferMode = TransferMode.Read;
        _transferOffset = 0;
        SetDataRequest(true);
    }

    private void BeginWriteSector()
    {
        if (!CheckMedia())
            return;

        if (_images.IsReadOnly(SelectedDrive))
        {
            SetError(StatusWriteProtect);
            return;
        }

        _defaultTransferBuffer.AsSpan().Clear();
        _transferBuffer = _defaultTransferBuffer;
        _transferMode = TransferMode.Write;
        _transferOffset = 0;
        SetDataRequest(true);
    }

    private void BeginReadAddress()
    {
        if (!DriveSelected || !_images.IsMounted(SelectedDrive))
        {
            SetError(StatusNotReady);
            return;
        }

        if (!_images.TryGetSectorHeader(SelectedDrive, PhysicalTrack, SelectedSide, Sector, out var header) &&
            !_images.TryGetSectorHeader(SelectedDrive, PhysicalTrack, SelectedSide, 1, out header))
        {
            SetError(StatusRecordNotFound);
            return;
        }

        _transferBuffer = new[]
        {
            (byte)header.Track,
            (byte)header.Side,
            (byte)header.Id,
            header.SizeCode,
            (byte)0,
            (byte)0
        };
        _transferMode = TransferMode.Read;
        _transferOffset = 0;
        SetDataRequest(true);
    }

    private void BeginReadTrack()
    {
        if (!DriveSelected || !_images.IsMounted(SelectedDrive))
        {
            SetError(StatusNotReady);
            return;
        }

        _transferBuffer = new byte[ModelIIIDiskGeometry.SectorsPerTrack * ModelIIIDiskGeometry.BytesPerSector];
        if (!_images.ReadTrack(SelectedDrive, PhysicalTrack, SelectedSide, _transferBuffer))
        {
            SetError(StatusRecordNotFound);
            return;
        }

        _transferMode = TransferMode.Read;
        _transferOffset = 0;
        SetDataRequest(true);
    }

    private void BeginWriteTrack()
    {
        if (!DriveSelected || !_images.IsMounted(SelectedDrive))
        {
            SetError(StatusNotReady);
            return;
        }

        if (_images.IsReadOnly(SelectedDrive))
        {
            SetError(StatusWriteProtect);
            return;
        }

        _transferBuffer = new byte[ModelIIIDiskGeometry.SectorsPerTrack * ModelIIIDiskGeometry.BytesPerSector];
        _transferMode = TransferMode.WriteTrack;
        _transferOffset = 0;
        SetDataRequest(true);
    }

    private bool CheckMedia()
    {
        if (!DriveSelected || !_images.IsMounted(SelectedDrive))
        {
            SetError(StatusNotReady);
            return false;
        }

        if (!ModelIIIDiskGeometry.IsValidSector(
                PhysicalTrack,
                SelectedSide,
                Sector,
                ModelIIIDiskGeometry.MaximumSides))
        {
            SetError(StatusRecordNotFound);
            return false;
        }

        return true;
    }

    private void CompleteCommand()
    {
        _pendingCommand = PendingCommand.None;
        _transferMode = TransferMode.None;
        _transferOffset = 0;
        _status &= unchecked((byte)~StatusBusy);
        SetDataRequest(false);
        SetInterruptRequest(true);
    }

    private void SetError(byte statusBit)
    {
        _pendingCommand = PendingCommand.None;
        _transferMode = TransferMode.None;
        _transferOffset = 0;
        _status |= statusBit;
        _status &= unchecked((byte)~StatusBusy);
        SetDataRequest(false);
        SetInterruptRequest(true);
    }

    private void ClearTransfer()
    {
        _pendingCommand = PendingCommand.None;
        _transferMode = TransferMode.None;
        _transferOffset = 0;
        _transferBuffer = _defaultTransferBuffer;
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

    private static byte ClampTrack(byte track) =>
        (byte)Math.Min((int)track, ModelIIIDiskGeometry.Tracks - 1);

    private enum TransferMode
    {
        None,
        Read,
        Write,
        WriteTrack
    }

    private enum PendingCommand
    {
        None,
        Restore,
        Seek,
        Step,
        StepUpdate,
        StepIn,
        StepInUpdate,
        StepOut,
        StepOutUpdate,
        ReadSector,
        WriteSector,
        ReadAddress,
        ReadTrack,
        WriteTrack
    }
}
