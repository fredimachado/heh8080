using Heh8080.Devices;
using Heh8080.UI.ViewModels;

namespace Heh8080.Tests;

public sealed class MainViewModelCp500Rs232Tests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _viewModel = new(
        new FileDiskImageProvider(),
        StatusRom(),
        new byte[Cp500CharacterGenerator.ImageSize]);

    public void Dispose() => _viewModel.Dispose();

    [Fact]
    public async Task ApplyKeepsDisksAndSpeedAndInstallsTheBoard()
    {
        await _viewModel.MountCp500Disk(1, new byte[ModelIIIDiskGeometry.DiskSizeBytes], "data.dsk", null);
        await _viewModel.SetCp500Speed(4);
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
        WaitUntil(() => Cell(1) == 0xFF);

        await _viewModel.SetCp500Speed(double.PositiveInfinity);
        await _viewModel.SetCp500Rs232(new Rs232Attachment(true, null));

        Assert.Equal(double.PositiveInfinity, _viewModel.Cp500SpeedFactor);
        Assert.Equal("data.dsk", _viewModel.GetDriveLabel(1));
        WaitUntil(() => Cell(1) == 0x02);
        byte start = Cell(0);
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while ((byte)(Cell(0) - start) < 90)
        {
            Assert.True(DateTime.UtcNow < deadline);
            Thread.Sleep(5);
        }
    }

    [Fact]
    public async Task ARejectedCableLeavesTheRunningMachine()
    {
        var endpoint = new TrackingEndpoint();
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
        WaitUntil(() => Cell(1) == 0xFF);
        await _viewModel.SetCp500Speed(4);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _viewModel.SetCp500Rs232(new Rs232Attachment(false, endpoint)));

        Assert.False(endpoint.Disposed);
        Assert.Equal(4, _viewModel.Cp500SpeedFactor);
        Assert.Equal(0xFF, Cell(1));
        endpoint.Dispose();
    }

    [Fact]
    public async Task HardDiskRebuildKeepsRs232AndTheCableRebuildKeepsTheDisk()
    {
        using var viewModel = new MainViewModel(
            new FileDiskImageProvider(),
            PortsRom(),
            new byte[Cp500CharacterGenerator.ImageSize]);
        var endpoint = new TrackingEndpoint();
        await viewModel.SwitchMachine(EmulatedMachine.Cp500);
        WaitUntil(() => viewModel.Cp500Video!.ReadCharacter(1, 0) == 0xFF);

        await viewModel.SetCp500Rs232(new Rs232Attachment(true, endpoint));
        WaitUntil(() => viewModel.Cp500Video!.ReadCharacter(1, 0) == 0x02);
        Assert.Equal(0xFF, viewModel.Cp500Video!.ReadCharacter(2, 0));

        await viewModel.SetCp500HardDisk(true);
        WaitUntil(() => viewModel.Cp500Video!.ReadCharacter(2, 0) == 0x40);
        Assert.Equal(0x02, viewModel.Cp500Video!.ReadCharacter(1, 0));
        Assert.False(endpoint.Disposed);
        Assert.True(viewModel.Cp500HardDiskInstalled);

        byte[] image = new byte[512];
        image[0] = 0x56;
        image[1] = 0xCB;
        image[2] = 0x10;
        image[26] = 1;
        image[28] = 1;
        image[29] = 1;
        await viewModel.MountCp500HardDisk(new byte[8], "bad.hdv", null);
        Assert.Null(viewModel.HardDiskName);
        Assert.Contains("Could not insert", viewModel.StatusText);
        await viewModel.MountCp500HardDisk(image, "disk.hdv", null);
        Assert.Equal("disk.hdv", viewModel.HardDiskName);
        Assert.Equal(0x02, viewModel.Cp500Video!.ReadCharacter(1, 0));

        await viewModel.SetCp500Rs232(new Rs232Attachment(true, endpoint));
        WaitUntil(() => viewModel.Cp500Video!.ReadCharacter(2, 0) == 0x40);
        Assert.Equal(0x02, viewModel.Cp500Video!.ReadCharacter(1, 0));
        Assert.False(endpoint.Disposed);
        Assert.Equal("disk.hdv", viewModel.HardDiskName);

        await viewModel.EjectCp500HardDisk();
        Assert.Null(viewModel.HardDiskName);
        await viewModel.SetCp500HardDisk(false);
        WaitUntil(() => viewModel.Cp500Video!.ReadCharacter(2, 0) == 0xFF);
        Assert.Equal(0x02, viewModel.Cp500Video!.ReadCharacter(1, 0));
        Assert.False(viewModel.Cp500HardDiskInstalled);
        Assert.False(endpoint.Disposed);
    }

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

    private static byte[] PortsRom()
    {
        byte[] program =
        [
            0xFB,
            0xDB, 0xE8,
            0x32, 0x01, 0x3C,
            0xDB, 0xCF,
            0x32, 0x02, 0x3C,
            0x18, 0xFE,
        ];
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        program.CopyTo(rom, 0);
        StatusHandler().CopyTo(rom, 0x0038);
        return rom;
    }

    private static byte[] StatusRom()
    {
        byte[] program =
        [
            0xFB,                   // EI
            0xDB, 0xE8,             // IN A,(E8h)
            0x32, 0x01, 0x3C,       // LD (3C01h),A
            0x18, 0xFE,             // JR $
        ];
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        program.CopyTo(rom, 0);
        StatusHandler().CopyTo(rom, 0x0038);
        return rom;
    }

    private static byte[] StatusHandler() =>
    [
        0xDB, 0xEC,
        0x21, 0x00, 0x3C,
        0x34,
        0xFB,
        0xED, 0x4D,
    ];

    private sealed class TrackingEndpoint : ISerialEndpoint
    {
        public bool Disposed { get; private set; }

        public ModemInputs Inputs => ModemInputs.Idle;

        public bool TryRead(out byte value)
        {
            value = 0;
            return false;
        }

        public void Write(byte value)
        {
        }

        public void AdvanceCycles(long cycles)
        {
        }

        public void Dispose() => Disposed = true;
    }
}
