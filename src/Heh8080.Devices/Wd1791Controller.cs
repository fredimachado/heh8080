using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// WD1791-compatible floppy controller for CP-500 Model III geometry.
/// It implements restore, seek/step, read-sector, write-sector, and force-interrupt commands.
/// </summary>
public sealed class Wd1791Controller : IIoDevice
{
    /// <summary>Command/status register port.</summary>
    public const byte CommandStatusPort = 0xE0;

    /// <summary>Track register port.</summary>
    public const byte TrackPort = 0xE1;

    /// <summary>Sector register port.</summary>
    public const byte SectorPort = 0xE2;

    /// <summary>Data register port.</summary>
    public const byte DataPort = 0xE3;

    private const byte LegacyCommandStatusPort = 0xF0;
    private const byte LegacyTrackPort = 0xF1;
    private const byte LegacySectorPort = 0xF2;
    private const byte LegacyDataPort = 0xF3;

    private const byte StatusBusy = 0x01;
    private const byte StatusDataRequest = 0x02;
    private const byte StatusRecordNotFound = 0x10;
    private const byte StatusWriteProtect = 0x40;
    private const byte StatusNotReady = 0x80;

    private readonly IModelIIIDiskImageProvider _images;
    private readonly byte[] _transferBuffer = new byte[ModelIIIDiskGeometry.BytesPerSector];
    private TransferMode _transferMode;
    private int _transferOffset;
    private bool _stepDirectionIn = true;
    private byte _status;

    /// <summary>Creates a controller that operates on the supplied storage provider.</summary>
    public Wd1791Controller(IModelIIIDiskImageProvider images)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
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

    /// <summary>Raised whenever DRQ changes.</summary>
    public event Action<bool>? DataRequestChanged;

    /// <summary>Raised whenever INTRQ changes.</summary>
    public event Action<bool>? InterruptRequestChanged;

    /// <summary>Registers the controller at E0h-E3h, retaining F0h-F3h as a compatibility alias.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, CommandStatusPort, DataPort);
        bus.Register(this, LegacyCommandStatusPort, LegacyDataPort);
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
    }

    public byte In(byte port) => port switch
    {
        CommandStatusPort or LegacyCommandStatusPort => ReadStatus(),
        TrackPort or LegacyTrackPort => Track,
        SectorPort or LegacySectorPort => Sector,
        DataPort or LegacyDataPort => ReadData(),
        _ => 0xFF
    };

    public void Out(byte port, byte value)
    {
        switch (port)
        {
            case CommandStatusPort:
            case LegacyCommandStatusPort:
                ExecuteCommand(value);
                break;
            case TrackPort:
            case LegacyTrackPort:
                Track = value;
                break;
            case SectorPort:
            case LegacySectorPort:
                Sector = value;
                break;
            case DataPort:
            case LegacyDataPort:
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
        if (_transferOffset == _transferBuffer.Length)
            CompleteCommand();

        return Data;
    }

    private void WriteData(byte value)
    {
        Data = value;
        if (_transferMode != TransferMode.Write || !DataRequest)
            return;

        _transferBuffer[_transferOffset++] = value;
        if (_transferOffset != _transferBuffer.Length)
            return;

        if (!_images.WriteSector(SelectedDrive, PhysicalTrack, SelectedSide, Sector, _transferBuffer))
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
                PhysicalTrack = 0;
                Track = 0;
                CompleteCommand();
                break;
            case 0x10:
                PhysicalTrack = ClampTrack(Data);
                Track = PhysicalTrack;
                CompleteCommand();
                break;
            case 0x20:
            case 0x30:
                Step(_stepDirectionIn, (command & 0x10) != 0);
                break;
            case 0x40:
            case 0x50:
                _stepDirectionIn = true;
                Step(true, (command & 0x10) != 0);
                break;
            case 0x60:
            case 0x70:
                _stepDirectionIn = false;
                Step(false, (command & 0x10) != 0);
                break;
            case 0x80:
            case 0x90:
                BeginReadSector();
                break;
            case 0xA0:
            case 0xB0:
                BeginWriteSector();
                break;
            case 0xD0:
                CompleteCommand();
                break;
            default:
                SetError(StatusRecordNotFound);
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

        if (!_images.ReadSector(SelectedDrive, PhysicalTrack, SelectedSide, Sector, _transferBuffer))
        {
            SetError(StatusRecordNotFound);
            return;
        }

        _transferMode = TransferMode.Read;
        _transferOffset = 0;
        _status |= StatusBusy;
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

        if (!ModelIIIDiskGeometry.IsValidSector(
                PhysicalTrack,
                SelectedSide,
                Sector,
                ModelIIIDiskGeometry.MaximumSides))
        {
            SetError(StatusRecordNotFound);
            return;
        }

        _transferBuffer.AsSpan().Clear();
        _transferMode = TransferMode.Write;
        _transferOffset = 0;
        _status |= StatusBusy;
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
        _transferMode = TransferMode.None;
        _transferOffset = 0;
        _status &= unchecked((byte)~StatusBusy);
        SetDataRequest(false);
        SetInterruptRequest(true);
    }

    private void SetError(byte statusBit)
    {
        _transferMode = TransferMode.None;
        _transferOffset = 0;
        _status |= statusBit;
        _status &= unchecked((byte)~StatusBusy);
        SetDataRequest(false);
        SetInterruptRequest(true);
    }

    private void ClearTransfer()
    {
        _transferMode = TransferMode.None;
        _transferOffset = 0;
        SetDataRequest(false);
    }

    private void SetDataRequest(bool value)
    {
        bool wasSet = DataRequest;
        if (value)
            _status |= StatusDataRequest;
        else
            _status &= unchecked((byte)~StatusDataRequest);

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

    private static byte ClampTrack(byte track) =>
        (byte)Math.Min((int)track, ModelIIIDiskGeometry.Tracks - 1);

    private enum TransferMode
    {
        None,
        Read,
        Write
    }
}
