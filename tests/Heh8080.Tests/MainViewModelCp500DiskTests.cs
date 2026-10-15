using Heh8080.Devices;
using Heh8080.UI.ViewModels;

namespace Heh8080.Tests;

/// <summary>
/// The CP-500 disk workflow of the desktop view model, run against a tiny ROM that writes sector 1 of drive 0
/// (every byte 5Ah), shows "D" at video cell 0 when done, then counts in cell 1 so tests can see the CPU run.
/// </summary>
public sealed class MainViewModelCp500DiskTests : IDisposable
{
    private const int MarkerCell = 0;
    private const int CounterCell = 1;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly List<byte[]> _saved = [];
    private readonly MainViewModel _viewModel = new(new FileDiskImageProvider(), SectorWriterRom());

    public void Dispose() => _viewModel.Dispose();

    private static byte[] SectorWriterRom()
    {
        byte[] program =
        [
            0x3E, 0x81, 0xD3, 0xF4,         // LD A,81h : OUT (F4h),A   select drive 0, MFM
            0xAF, 0xD3, 0xF1,               // XOR A    : OUT (F1h),A   track 0
            0x3E, 0x01, 0xD3, 0xF2,         // LD A,1   : OUT (F2h),A   sector 1
            0x3E, 0xA0, 0xD3, 0xF0,         // LD A,A0h : OUT (F0h),A   WRITE SECTOR
            0x06, 0x00, 0x3E, 0x5A,         // LD B,0   : LD A,5Ah
            0xD3, 0xF3, 0x10, 0xFC,         // OUT (F3h),A : DJNZ -4    256 data bytes
            0x3E, 0x44, 0x32, 0x00, 0x3C,   // LD A,'D' : LD (3C00h),A  done marker
            0x21, 0x01, 0x3C,               // LD HL,3C01h
            0x34, 0x18, 0xFD                // INC (HL) : JR -3         running counter
        ];
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        program.CopyTo(rom, 0);
        return rom;
    }

    private static byte[] RawDisk() => new byte[ModelIIIDiskGeometry.DiskSizeBytes];

    private static bool WroteTheSector(byte[] rawImage) =>
        rawImage.AsSpan(0, ModelIIIDiskGeometry.BytesPerSector).ToArray().All(value => value == 0x5A);

    private byte Cell(int column) => _viewModel.Cp500Video!.ReadCharacter(column, 0);

    private static void WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the emulated CPU.");
            Thread.Sleep(10);
        }
    }

    private async Task RunTheSectorWriter(bool readOnly = false)
    {
        await _viewModel.MountCp500Disk(0, RawDisk(), "work.dsk", _saved.Add, readOnly);
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
        WaitUntil(() => Cell(MarkerCell) == (byte)'D');
    }

    [Fact]
    public async Task DriveLabelsShowTheCp500DrivesOnlyWhileCp500IsActive()
    {
        await _viewModel.MountCp500Disk(1, RawDisk(), "data.dsk", null);
        _viewModel.UpdateDriveStatus(0, "lolos.img");

        Assert.Equal("lolos.img", _viewModel.GetDriveLabel(0));
        Assert.Null(_viewModel.GetDriveLabel(1));
        Assert.True(_viewModel.IsDriveInstalled(3));

        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);

        Assert.Null(_viewModel.GetDriveLabel(0));
        Assert.Equal("data.dsk", _viewModel.GetDriveLabel(1));
        Assert.True(_viewModel.IsDriveInstalled(1));
        Assert.False(_viewModel.IsDriveInstalled(2));
    }

    [Fact]
    public async Task MountingIntoARunningMachineKeepsItRunning()
    {
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
        WaitUntil(() => Cell(MarkerCell) == (byte)'D');

        await _viewModel.MountCp500Disk(0, RawDisk(), "late.dsk", _saved.Add);

        byte counter = Cell(CounterCell);
        WaitUntil(() => Cell(CounterCell) != counter);
        Assert.Equal("late.dsk", _viewModel.GetDriveLabel(0));
        Assert.Equal("Inserted late.dsk in drive 0", _viewModel.StatusText);
    }

    [Fact]
    public async Task UnmountingWritesBackADiskTheProgramModifiedAndEmptiesTheDrive()
    {
        await RunTheSectorWriter();

        await _viewModel.UnmountDiskCommand.ExecuteAsync(0);

        Assert.True(WroteTheSector(Assert.Single(_saved)));
        Assert.Null(_viewModel.GetDriveLabel(0));
        byte counter = Cell(CounterCell);
        WaitUntil(() => Cell(CounterCell) != counter);
    }

    [Fact]
    public async Task ReadOnlyDisksAreNeverWrittenBack()
    {
        await RunTheSectorWriter(readOnly: true);

        await _viewModel.UnmountDiskCommand.ExecuteAsync(0);

        Assert.Empty(_saved);
        Assert.Null(_viewModel.GetDriveLabel(0));
    }

    [Fact]
    public async Task SwitchingBackToCpmWritesTheDisksBackOnceAndKeepsThemMounted()
    {
        await RunTheSectorWriter();

        await _viewModel.SwitchMachine(EmulatedMachine.Cpm);
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);

        Assert.True(WroteTheSector(Assert.Single(_saved)));
        Assert.Equal("work.dsk", _viewModel.GetDriveLabel(0));
    }

    [Fact]
    public async Task ShuttingDownWritesBackModifiedDisks()
    {
        await RunTheSectorWriter();

        _viewModel.Dispose();

        Assert.True(WroteTheSector(Assert.Single(_saved)));
    }

    [Fact]
    public async Task AFailedWriteBackKeepsTheDiskAndTellsTheUser()
    {
        await _viewModel.MountCp500Disk(0, RawDisk(), "work.dsk", _ => throw new IOException("disk full"));
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
        WaitUntil(() => Cell(MarkerCell) == (byte)'D');

        await _viewModel.UnmountDiskCommand.ExecuteAsync(0);

        Assert.Equal("work.dsk", _viewModel.GetDriveLabel(0));
        Assert.Contains("disk full", _viewModel.StatusText);
    }

    [Fact]
    public async Task AnImageThatIsNotADiskIsRejectedWithoutChangingTheDrive()
    {
        await _viewModel.MountCp500Disk(0, new byte[100], "junk.jv3", _saved.Add);

        Assert.Null(_viewModel.GetDriveLabel(0));
        Assert.StartsWith("Could not insert junk.jv3", _viewModel.StatusText);
    }
}
