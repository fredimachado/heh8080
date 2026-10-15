using Heh8080.Devices;
using Heh8080.UI.ViewModels;

namespace Heh8080.Tests;

/// <summary>
/// Whether the desktop view model starts and resets the CP-500 holding BREAK, run against a tiny ROM that counts its
/// boots in video cell 0, records whether BREAK is down right after reset in cell 1 (04h when it is) and finishes by
/// copying the boot count to cell 2.
/// </summary>
public sealed class MainViewModelCp500BootTests : IDisposable
{
    private const byte BreakSeen = 0x04;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly MainViewModel _viewModel = new(new FileDiskImageProvider(), BootProbeRom(), new byte[Cp500CharacterGenerator.ImageSize]);

    public void Dispose() => _viewModel.Dispose();

    private static byte[] BootProbeRom()
    {
        byte[] program =
        [
            0x21, 0x00, 0x3C,       // LD HL,3C00h
            0x34,                   // INC (HL)          boot count
            0x3A, 0x40, 0x38,       // LD A,(3840h)      keyboard row 6
            0xE6, 0x04,             // AND 04h           column 2 is BREAK
            0x32, 0x01, 0x3C,       // LD (3C01h),A
            0x7E,                   // LD A,(HL)
            0x32, 0x02, 0x3C,       // LD (3C02h),A      probe finished
            0x18, 0xFE              // JR $
        ];
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        program.CopyTo(rom, 0);
        return rom;
    }

    private byte Cell(int column) => _viewModel.Cp500Video!.ReadCharacter(column, 0);

    private byte BreakSampleOfBoot(byte boot)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (Cell(2) != boot)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the emulated CPU.");
            Thread.Sleep(10);
        }

        return Cell(1);
    }

    private Task MountOnDriveZero() =>
        _viewModel.MountCp500Disk(0, new byte[ModelIIIDiskGeometry.DiskSizeBytes], "boot.dsk", null);

    [Fact]
    public async Task StartingWithAnEmptyDriveZeroHoldsBreak()
    {
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);

        Assert.Equal(BreakSeen, BreakSampleOfBoot(1));
    }

    [Fact]
    public async Task StartingWithADiskInDriveZeroDoesNotHoldBreak()
    {
        await MountOnDriveZero();
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);

        Assert.Equal(0, BreakSampleOfBoot(1));
    }

    [Fact]
    public async Task ResettingWithAnEmptyDriveZeroHoldsBreak()
    {
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
        BreakSampleOfBoot(1);

        await _viewModel.Reset();

        Assert.Equal(BreakSeen, BreakSampleOfBoot(2));
    }

    [Fact]
    public async Task ResettingAfterMountingADiskInDriveZeroBootsFromItWithoutBreak()
    {
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
        Assert.Equal(BreakSeen, BreakSampleOfBoot(1));
        await MountOnDriveZero();

        await _viewModel.Reset();

        Assert.Equal(0, BreakSampleOfBoot(2));
    }
}

public sealed class MainViewModelCp500AvailabilityTests
{
    private static readonly byte[] SystemRom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
    private static readonly byte[] CharacterRom = new byte[Cp500CharacterGenerator.ImageSize];

    [Fact]
    public void Cp500NeedsTheSystemRomAndTheCharacterRom()
    {
        Assert.True(new MainViewModel(new FileDiskImageProvider(), SystemRom, CharacterRom).IsCp500Available);
        Assert.False(new MainViewModel(new FileDiskImageProvider(), SystemRom, null).IsCp500Available);
        Assert.False(new MainViewModel(new FileDiskImageProvider(), null, CharacterRom).IsCp500Available);
        Assert.False(new MainViewModel(new FileDiskImageProvider(), SystemRom, new byte[100]).IsCp500Available);
    }

    [Fact]
    public async Task SelectingCp500WithoutBothRomsNamesBothFiles()
    {
        var viewModel = new MainViewModel(new FileDiskImageProvider(), SystemRom);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.SwitchMachine(EmulatedMachine.Cp500));

        Assert.Contains("cp500.rom", error.Message);
        Assert.Contains("cp500-chargen.rom", error.Message);
        Assert.False(viewModel.IsCp500);
    }
}