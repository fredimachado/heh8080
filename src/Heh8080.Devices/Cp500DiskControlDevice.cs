using System.Numerics;
using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Model III write-only disk-control latch mirrored across F4h-F7h.
/// It selects a drive/head/density and holds the CPU in WAIT until WD1791 DRQ or INTRQ without coupling devices to a CPU implementation.
/// Reads of F4h-F7h belong to the CP-500 ROM A11 flip-flop.
/// </summary>
public sealed class Cp500DiskControlDevice : IIoDevice
{
    /// <summary>First mirrored disk-control port.</summary>
    public const byte ControlPort = 0xF4;

    /// <summary>Last mirrored disk-control port.</summary>
    public const byte LastControlPort = 0xF7;

    private const byte DriveSelectBits = 0x0F;
    private const byte SideBit = 0x10;
    private const byte WaitBit = 0x40;
    private const byte MfmBit = 0x80;

    private readonly Wd1791Controller _controller;

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
    public byte DriveSelectMask => (byte)(ControlLatch & DriveSelectBits);

    /// <summary>Selected physical side from latch bit 4.</summary>
    public int SelectedSide => (ControlLatch & SideBit) != 0 ? 1 : 0;

    /// <summary>Whether latch bit 6 requests CPU WAIT until DRQ or INTRQ.</summary>
    public bool WaitRequested => (ControlLatch & WaitBit) != 0;

    /// <summary>Recording density selected by latch bit 7: MFM when set, FM when clear.</summary>
    public ModelIIIDiskDensity SelectedDensity =>
        (ControlLatch & MfmBit) != 0 ? ModelIIIDiskDensity.DoubleDensity : ModelIIIDiskDensity.SingleDensity;

    /// <summary>Whether the host should presently hold a CPU in WAIT.</summary>
    public bool WaitActive { get; private set; }

    /// <summary>Registers the write side of F4h-F7h.</summary>
    public void Register(IoBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.RegisterOutput(this, ControlPort, LastControlPort);
    }

    /// <summary>Resets the latch and disconnects all disk drives without changing mounted media.</summary>
    public void Reset()
    {
        ControlLatch = 0;
        ApplyLatch();
        WaitActive = false;
    }

    /// <summary>The latch is write-only.</summary>
    public byte In(byte port) => 0xFF;

    /// <summary>Writes the control latch through any F4h-F7h mirror.</summary>
    public void Out(byte port, byte value)
    {
        if (!IsControlPort(port))
            return;

        ControlLatch = value;
        ApplyLatch();
        WaitActive = ShouldAssertWait();
    }

    private void ApplyLatch()
    {
        byte drives = DriveSelectMask;
        if (drives == 0)
            _controller.DeselectDrive();
        else
            _controller.SelectDrive(BitOperations.TrailingZeroCount(drives));

        _controller.SelectSide(SelectedSide);
        _controller.SelectDensity(SelectedDensity);
    }

    private void OnDataRequestChanged(bool value)
    {
        if (value)
            WaitActive = false;
    }

    private void OnInterruptRequestChanged(bool value)
    {
        if (value)
            WaitActive = false;
    }

    private bool ShouldAssertWait() =>
        WaitRequested &&
        DriveSelectMask != 0 &&
        (_controller.Status & Wd1791Controller.StatusBusy) != 0 &&
        !_controller.DataRequest &&
        !_controller.InterruptRequest;

    private static bool IsControlPort(byte port) => port is >= ControlPort and <= LastControlPort;
}
