using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Model III disk-control latch mirrored across F4h-F7h.
/// It selects a drive/head and exposes WD1791 handshaking without coupling devices to a CPU implementation.
/// </summary>
public sealed class Cp500DiskControlDevice : IIoDevice
{
    /// <summary>First mirrored disk-control port.</summary>
    public const byte ControlPort = 0xE4;

    /// <summary>Last mirrored disk-control port.</summary>
    public const byte LastControlPort = 0xE7;

    private const byte LegacyControlPort = 0xF4;
    private const byte LegacyLastControlPort = 0xF7;

    private readonly Wd1791Controller _controller;
    private bool _waitActive;

    /// <summary>Creates a disk-control latch attached to a WD1791 controller.</summary>
    public Cp500DiskControlDevice(Wd1791Controller controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _controller.DataRequestChanged += OnDataRequestChanged;
        _controller.InterruptRequestChanged += OnInterruptRequestChanged;
    }

    /// <summary>Last value written to the disk-control latch.</summary>
    public byte ControlLatch { get; private set; }

    /// <summary>Active drive-select lines represented by latch bits 0-3.</summary>
    public byte DriveSelectMask => (byte)(ControlLatch & 0x0F);

    /// <summary>Selected physical side from latch bit 4.</summary>
    public int SelectedSide => (ControlLatch >> 4) & 0x01;

    /// <summary>Whether latch bit 6 requests CPU WAIT until DRQ or INTRQ.</summary>
    public bool WaitRequested => (ControlLatch & 0x40) != 0;

    /// <summary>Whether the host should presently hold a CPU in WAIT.</summary>
    public bool WaitActive => _waitActive;

    /// <summary>Current WD1791 DRQ line.</summary>
    public bool DataRequest => _controller.DataRequest;

    /// <summary>Current WD1791 INTRQ line.</summary>
    public bool InterruptRequest => _controller.InterruptRequest;

    /// <summary>Raised after the latch selects a new drive, side, or WAIT request.</summary>
    public event Action<byte>? ControlLatchChanged;

    /// <summary>Raised whenever the WD1791 DRQ line changes.</summary>
    public event Action<bool>? DataRequestChanged;

    /// <summary>Raised whenever the WD1791 INTRQ line changes.</summary>
    public event Action<bool>? InterruptRequestChanged;

    /// <summary>Raised whenever the host-facing WAIT state changes.</summary>
    public event Action<bool>? WaitActiveChanged;

    /// <summary>Registers the mirrored control latch at E4h-E7h, retaining F4h-F7h as a compatibility alias.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.Register(this, ControlPort, LastControlPort);
        bus.Register(this, LegacyControlPort, LegacyLastControlPort);
    }

    /// <summary>Resets the latch and disconnects all disk drives without changing mounted media.</summary>
    public void Reset()
    {
        bool changed = ControlLatch != 0;
        ControlLatch = 0;
        _controller.DeselectDrive();
        _controller.SelectSide(0);
        SetWaitActive(false);

        if (changed)
            ControlLatchChanged?.Invoke(ControlLatch);
    }

    /// <summary>Reads the current latched control state from any F4h-F7h mirror.</summary>
    public byte In(byte port) => IsControlPort(port) ? ControlLatch : (byte)0xFF;

    /// <summary>Writes the control latch through any F4h-F7h mirror.</summary>
    public void Out(byte port, byte value)
    {
        if (!IsControlPort(port))
            return;

        ControlLatch = value;
        byte drives = DriveSelectMask;
        if (drives == 0)
        {
            _controller.DeselectDrive();
        }
        else
        {
            _controller.SelectDrive(GetLowestSelectedDrive(drives));
        }

        _controller.SelectSide(SelectedSide);
        SetWaitActive(WaitRequested && !_controller.DataRequest && !_controller.InterruptRequest);
        ControlLatchChanged?.Invoke(ControlLatch);
    }

    private void OnDataRequestChanged(bool value)
    {
        if (value)
            SetWaitActive(false);

        DataRequestChanged?.Invoke(value);
    }

    private void OnInterruptRequestChanged(bool value)
    {
        if (value)
            SetWaitActive(false);

        InterruptRequestChanged?.Invoke(value);
    }

    private void SetWaitActive(bool value)
    {
        if (_waitActive == value)
            return;

        _waitActive = value;
        WaitActiveChanged?.Invoke(value);
    }

    private static bool IsControlPort(byte port) =>
        port is >= ControlPort and <= LastControlPort ||
        port is >= LegacyControlPort and <= LegacyLastControlPort;

    private static int GetLowestSelectedDrive(byte driveMask)
    {
        for (int drive = 0; drive < MemoryModelIIIDiskImageProvider.MaxDrives; drive++)
        {
            if ((driveMask & (1 << drive)) != 0)
                return drive;
        }

        throw new ArgumentOutOfRangeException(nameof(driveMask));
    }
}
