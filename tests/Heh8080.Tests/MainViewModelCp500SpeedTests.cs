using System.Diagnostics;
using Heh8080.Devices;
using Heh8080.UI.ViewModels;

namespace Heh8080.Tests;

/// <summary>
/// Fast-forward through the desktop view model's real run loop, with a tiny ROM that counts 30 Hz timer interrupts in
/// video cell 0. Ninety ticks are three seconds of emulated time, so finishing them in two host seconds is only
/// possible above real time.
/// </summary>
public sealed class MainViewModelCp500SpeedTests : IDisposable
{
    private const int Ticks = 90;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    private readonly MainViewModel _viewModel = new(new FileDiskImageProvider(), TickCounterRom(), new byte[Cp500CharacterGenerator.ImageSize]);

    public void Dispose() => _viewModel.Dispose();

    private static byte[] TickCounterRom()
    {
        byte[] main = [0xFB, 0x18, 0xFE];                                     // EI ; JR $
        byte[] handler = [0xDB, 0xEC, 0x21, 0x00, 0x3C, 0x34, 0xFB, 0xED, 0x4D]; // IN A,(ECh) ; LD HL,3C00h ; INC (HL) ; EI ; RETI
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        main.CopyTo(rom, 0x0000);
        handler.CopyTo(rom, 0x0038);
        return rom;
    }

    private byte TickCount() => _viewModel.Cp500Video!.ReadCharacter(0, 0);

    private bool CountsTicksWithinDeadline()
    {
        byte start = TickCount();
        var host = Stopwatch.StartNew();
        while (host.Elapsed < Deadline)
        {
            if ((byte)(TickCount() - start) >= Ticks)
                return true;
            Thread.Sleep(5);
        }

        return false;
    }

    [Fact]
    public async Task RealTimeTakesTheFullEmulatedDuration()
    {
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);

        Assert.False(CountsTicksWithinDeadline());
    }

    [Fact]
    public async Task MaximumSpeedChosenBeforeStartingRunsFasterThanRealTime()
    {
        await _viewModel.SetCp500Speed(double.PositiveInfinity);
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);

        Assert.True(CountsTicksWithinDeadline());
    }

    [Fact]
    public async Task MaximumSpeedChosenWhileRunningTakesEffectAndSurvivesReset()
    {
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
        await _viewModel.SetCp500Speed(double.PositiveInfinity);
        await _viewModel.Reset();

        Assert.True(CountsTicksWithinDeadline());
        Assert.Equal("CP-500 reset", _viewModel.StatusText);
    }

    [Fact]
    public async Task HoldingAKeyAtMaximumSpeedRunsInRealTimeUntilReleased()
    {
        await _viewModel.SetCp500Speed(double.PositiveInfinity);
        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);

        _viewModel.Cp500Keyboard!.SetKeyState(1, 1, pressed: true);
        Assert.False(CountsTicksWithinDeadline());

        _viewModel.Cp500Keyboard.SetKeyState(1, 1, pressed: false);
        Assert.True(CountsTicksWithinDeadline());
    }

    [Fact]
    public async Task SpeedIsReportedAndRejectsNonPositiveFactors()
    {
        await _viewModel.SetCp500Speed(4);

        Assert.Equal(4, _viewModel.Cp500SpeedFactor);
        Assert.Equal("CP-500 speed: 4x", _viewModel.StatusText);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _viewModel.SetCp500Speed(0));
        Assert.Equal(4, _viewModel.Cp500SpeedFactor);
    }
}
