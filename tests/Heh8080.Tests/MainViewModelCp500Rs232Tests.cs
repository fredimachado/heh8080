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

    private static byte[] StatusRom()
    {
        byte[] program =
        [
            0xFB,                   // EI
            0xDB, 0xE8,             // IN A,(E8h)
            0x32, 0x01, 0x3C,       // LD (3C01h),A
            0x18, 0xFE,             // JR $
        ];
        byte[] handler =
        [
            0xDB, 0xEC,             // IN A,(ECh)
            0x21, 0x00, 0x3C,       // LD HL,3C00h
            0x34,                   // INC (HL)
            0xFB,                   // EI
            0xED, 0x4D,             // RETI
        ];
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        program.CopyTo(rom, 0);
        handler.CopyTo(rom, 0x0038);
        return rom;
    }

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
