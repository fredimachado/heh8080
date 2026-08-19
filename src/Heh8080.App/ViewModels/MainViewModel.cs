using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heh8080.Core;
using Heh8080.Devices;
using Heh8080.Terminal;
using Heh8080.UI.Diagnostics;

namespace Heh8080.UI.ViewModels;

public enum DesktopMachineProfile
{
    Cpm,
    Cp500
}

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int Cp500InputSettleCycles = 100_000;

    private Emulator _emulator;
    private readonly IDiskImageProvider _diskProvider;
    private readonly Adm3aTerminal _terminal;
    private CpuType _cpuType = CpuType.ZilogZ80;
    private readonly byte[]? _cp500Rom;
    private Cp500Machine? _cp500Machine;
    private CancellationTokenSource? _cp500Cancellation;
    private Task? _cp500RunTask;
    private bool _cp500BootGateActive;
    private volatile bool _cp500BootPaused;
    private int _cp500ThrottleRebaseRequested;
    private int _cp500InputSettleCyclesRemaining;

    // Devices
    private readonly ConsolePortHandler _console;
    private FloppyDiskController _fdc;
    private MemoryManagementUnit _mmu;
    private readonly TimerDevice _timer;
    private readonly DelayDevice _delay;
    private readonly HardwareControlDevice _hwControl;
    private readonly PrinterPortHandler _printer;
    private readonly AuxiliaryPortHandler _aux;

    // Timer for 10ms interrupts
    private System.Threading.Timer? _interruptTimer;

    [ObservableProperty]
    private string _statusText = "Initializing...";

    [ObservableProperty]
    private string? _driveAPath;

    [ObservableProperty]
    private string? _driveBPath;

    [ObservableProperty]
    private string? _driveCPath;

    [ObservableProperty]
    private string? _driveDPath;

    public Adm3aTerminal Terminal => _terminal;
    public IDiskImageProvider DiskProvider => _diskProvider;
    public bool IsRunning => _emulator.IsRunning;
    public CpuType CpuType => _cpuType;
    public string CpuTypeName => _cpuType == CpuType.ZilogZ80 ? "Z80" : "8080";
    public DesktopMachineProfile MachineProfile { get; private set; } = DesktopMachineProfile.Cpm;
    public bool IsCp500 => MachineProfile == DesktopMachineProfile.Cp500;
    public bool IsCp500Available => _cp500Rom is { Length: > 0 };
    public Cp500VideoDevice? Cp500Video => _cp500Machine?.Video;
    public Cp500KeyboardDevice? Cp500Keyboard => _cp500Machine?.Keyboard;
    public event Action? MachineProfileChanged;

    public MainViewModel(IDiskImageProvider diskProvider, byte[]? cp500Rom = null)
    {
        _diskProvider = diskProvider;
        _cp500Rom = cp500Rom is { Length: > 0 } ? (byte[])cp500Rom.Clone() : null;

        // Initialize emulator with Z80 by default
        _emulator = new Emulator(_cpuType);
        _terminal = new Adm3aTerminal();

        // Create and register devices
        _console = new ConsolePortHandler(_terminal);
        _console.Register(_emulator.IoBus);

        _fdc = new FloppyDiskController(_diskProvider, _emulator.Memory);
        _fdc.Register(_emulator.IoBus);

        _mmu = new MemoryManagementUnit(_emulator.Memory);
        _mmu.Register(_emulator.IoBus);

        _timer = new TimerDevice();
        _timer.Register(_emulator.IoBus);
        _timer.SetInterruptCallback(OnTimerInterrupt);

        _delay = new DelayDevice();
        _delay.Register(_emulator.IoBus);
        _delay.SetDelayCallback(OnDelayRequested);

        _hwControl = new HardwareControlDevice();
        _hwControl.Register(_emulator.IoBus);
        _hwControl.SetResetCallback(OnResetRequested);
        _hwControl.SetHaltCallback(OnHaltRequested);

        _printer = new PrinterPortHandler(new NullPrinterDevice());
        _printer.Register(_emulator.IoBus);

        _aux = new AuxiliaryPortHandler(null);
        _aux.Register(_emulator.IoBus);

        // Wire up emulator events
        _emulator.Started += () => Dispatcher.UIThread.Post(() => StatusText = "Running");
        _emulator.Stopped += () => Dispatcher.UIThread.Post(() => StatusText = "Stopped");
        _emulator.Error += ex => Dispatcher.UIThread.Post(() => StatusText = $"Error: {ex.Message}");
    }

    /// <summary>
    /// Boot from the disk mounted on drive A:
    /// </summary>
    public void Boot()
    {
        try
        {
            if (!_diskProvider.IsMounted(0))
            {
                StatusText = "Ready - mount a disk to begin";
                WriteToTerminal("heh8080 - Intel 8080 Emulator\r\n");
                WriteToTerminal("FJM-3A Terminal Ready\r\n");
                WriteToTerminal("\r\nNo boot disk found. Click the FJM-3A logo\r\n");
                WriteToTerminal("to mount a disk image.\r\n");
                return;
            }

            // Load boot sector (track 0, sector 1) to 0x0000
            Span<byte> bootSector = stackalloc byte[128];
            if (_diskProvider.ReadSector(0, 0, 1, bootSector))
            {
                _emulator.Load(0x0000, bootSector);
                _emulator.Cpu.PC = 0x0000;
                _emulator.Cpu.SP = 0xFFFF;

                // Start the interrupt timer and emulator
                StartInterruptTimer();
                _emulator.Start();
                StatusText = "Booting...";
                return;
            }

            StatusText = "Boot failed - could not read boot sector";
        }
        catch (Exception ex)
        {
            StatusText = $"Boot failed: {ex.Message}";
            WriteToTerminal($"Boot error: {ex.Message}\r\n");
        }
    }

    public void NotifyCp500KeyInput()
    {
        Cp500DiagnosticTrace.Write("host-key-input");
        if (_cp500BootGateActive)
        {
            Interlocked.Exchange(ref _cp500ThrottleRebaseRequested, 1);
            Interlocked.Exchange(ref _cp500InputSettleCyclesRemaining, Cp500InputSettleCycles);
            _cp500BootPaused = false;
        }
    }

    private static string? GetCp500Prompt(Cp500Machine machine)
    {
        for (int row = Cp500VideoDevice.Rows - 1; row >= 0; row--)
        {
            if (ContainsCp500TextAtRow(machine, row, "Mem. usada ?"))
                return $"Mem. usada ?@{row}";
            if (ContainsCp500TextAtRow(machine, row, "Cass?"))
                return $"Cass?@{row}";
        }

        return null;
    }

    private static bool ContainsCp500TextAtRow(Cp500Machine machine, int row, string text)
    {
        for (int column = 0; column <= Cp500VideoDevice.Columns - text.Length; column++)
        {
            bool matches = true;
            for (int index = 0; index < text.Length; index++)
            {
                if (machine.Video.ReadCharacter(column + index, row) != text[index])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return true;
        }

        return false;
    }

    private static bool ContainsCp500Text(Cp500Machine machine, string text)
    {
        for (int row = 0; row < Cp500VideoDevice.Rows; row++)
        {
            for (int column = 0; column <= Cp500VideoDevice.Columns - text.Length; column++)
            {
                bool matches = true;
                for (int index = 0; index < text.Length; index++)
                {
                    if (machine.Video.ReadCharacter(column + index, row) != text[index])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return true;
            }
        }

        return false;
    }


    private void WriteToTerminal(string text)
    {
        foreach (char c in text)
            _terminal.WriteChar((byte)c);
    }

    private void StartInterruptTimer()
    {
        _interruptTimer = new System.Threading.Timer(
            _ => _timer.Tick(),
            null,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));
    }

    private void StopInterruptTimer()
    {
        _interruptTimer?.Dispose();
        _interruptTimer = null;
    }

    private void OnTimerInterrupt()
    {
        if (_emulator.IsRunning && _emulator.Cpu.InterruptsEnabled)
        {
            _emulator.Cpu.Interrupt(7); // RST 7 for timer
        }
    }

    private void OnDelayRequested(int units)
    {
        // Delay n x 10ms - just sleep on the CPU thread
        Thread.Sleep(units * 10);
    }

    private void OnResetRequested()
    {
        Dispatcher.UIThread.Post(async () => await Reset());
    }

    private void OnHaltRequested()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await _emulator.StopAsync();
            StopInterruptTimer();
            StatusText = "Halted";
        });
    }

    [RelayCommand]
    public async Task Reset()
    {
        if (IsCp500)
        {
            await StopCp500Async();
            _cp500Machine!.Keyboard.ReleaseAll();
            _cp500Machine.Reset();
            _cp500BootGateActive = true;
            _cp500BootPaused = false;
            _cp500InputSettleCyclesRemaining = 0;
            StartCp500();
            StatusText = "CP-500 reset";
            return;
        }

        await _emulator.StopAsync();
        StopInterruptTimer();
        _emulator.Reset();
        _terminal.Buffer.Clear();

        // Reload boot sector if disk is mounted
        if (_diskProvider.IsMounted(0))
        {
            Span<byte> bootSector = stackalloc byte[128];
            if (_diskProvider.ReadSector(0, 0, 1, bootSector))
            {
                _emulator.Load(0x0000, bootSector);
                _emulator.Cpu.PC = 0x0000;
                _emulator.Cpu.SP = 0xFFFF;
                StartInterruptTimer();
                _emulator.Start();
                StatusText = "Reset - rebooting...";
                return;
            }
        }

        StatusText = "Reset";
    }

    public async Task SwitchCpuType(CpuType newType)
    {
        if (IsCp500)
            return;

        if (newType == _cpuType) return;

        await _emulator.StopAsync();
        StopInterruptTimer();
        _emulator.Dispose();

        _cpuType = newType;
        _emulator = new Emulator(_cpuType);

        // Re-register all devices
        _console.Register(_emulator.IoBus);
        _fdc = new FloppyDiskController(_diskProvider, _emulator.Memory);
        _fdc.Register(_emulator.IoBus);
        _mmu = new MemoryManagementUnit(_emulator.Memory);
        _mmu.Register(_emulator.IoBus);
        _timer.Register(_emulator.IoBus);
        _delay.Register(_emulator.IoBus);
        _hwControl.Register(_emulator.IoBus);
        _printer.Register(_emulator.IoBus);
        _aux.Register(_emulator.IoBus);

        // Wire up emulator events
        _emulator.Started += () => Dispatcher.UIThread.Post(() => StatusText = "Running");
        _emulator.Stopped += () => Dispatcher.UIThread.Post(() => StatusText = "Stopped");
        _emulator.Error += ex => Dispatcher.UIThread.Post(() => StatusText = $"Error: {ex.Message}");

        _terminal.Buffer.Clear();

        // Reboot if disk is mounted
        if (_diskProvider.IsMounted(0))
        {
            Span<byte> bootSector = stackalloc byte[128];
            if (_diskProvider.ReadSector(0, 0, 1, bootSector))
            {
                _emulator.Load(0x0000, bootSector);
                _emulator.Cpu.PC = 0x0000;
                _emulator.Cpu.SP = 0xFFFF;
                StartInterruptTimer();
                _emulator.Start();
                StatusText = $"Switched to {CpuTypeName} - rebooting...";
                return;
            }
        }

        StatusText = $"Switched to {CpuTypeName}";
    }

    public async Task SwitchMachineProfile(DesktopMachineProfile profile)
    {
        if (profile == MachineProfile)
            return;

        if (profile == DesktopMachineProfile.Cp500)
        {
            if (_cp500Rom is not { Length: > 0 })
                throw new InvalidOperationException(
                    "CP-500 ROM not found. Supply cp500/CP500-1.BIN and restart the app.");

            await _emulator.StopAsync();
            StopInterruptTimer();
            if (_cp500Machine == null)
            {
                _cp500Machine = new Cp500Machine(Cp500MachineProfile.Original, _cp500Rom);
                AttachCp500Diagnostics(_cp500Machine);
            }
            _cp500Machine.Reset();
            Cp500DiagnosticTrace.Write("machine-reset profile=original");
            _cp500BootGateActive = true;
            _cp500BootPaused = false;
            _cp500InputSettleCyclesRemaining = 0;
            MachineProfile = profile;
            MachineProfileChanged?.Invoke();
            StartCp500();
            StatusText = "CP-500 running";
            return;
        }

        await StopCp500Async();
        _cp500Machine?.Keyboard.ReleaseAll();
        MachineProfile = profile;
        MachineProfileChanged?.Invoke();
        await Reset();
    }

    private void StartCp500()
    {
        _cp500Cancellation = new CancellationTokenSource();
        _cp500RunTask = Task.Run(() => RunCp500Async(_cp500Cancellation.Token));
    }

    private async Task RunCp500Async(CancellationToken cancellationToken)
    {
        Cp500Machine machine = _cp500Machine!;
        long heartbeatPeriod = Stopwatch.Frequency / 30;
        long nextHeartbeat = Stopwatch.GetTimestamp() + heartbeatPeriod;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_cp500BootPaused)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (Interlocked.Exchange(ref _cp500ThrottleRebaseRequested, 0) != 0)
                {
                    machine.Throttle.Reset();
                    Cp500DiagnosticTrace.Write("throttle-rebased");
                }

                const int batchCycleLimit = 2048;
                int executedCycles = 0;
                for (; executedCycles < batchCycleLimit && !cancellationToken.IsCancellationRequested;)
                    executedCycles += machine.Step();

                if (_cp500BootGateActive && !machine.Keyboard.HasPressedKeys &&
                    ConsumeCp500InputSettleCycles(executedCycles))
                {
                    if (ContainsCp500Text(machine, "READY"))
                    {
                        _cp500BootGateActive = false;
                        Cp500DiagnosticTrace.Write("boot-gate-complete prompt=READY");
                    }
                    else if (GetCp500Prompt(machine) is { } prompt)
                    {
                        _cp500BootPaused = true;
                        Cp500DiagnosticTrace.Write($"boot-paused prompt={prompt}");
                    }
                }

                long now = Stopwatch.GetTimestamp();
                while (now >= nextHeartbeat)
                {
                    machine.TickInterrupt();
                    nextHeartbeat += heartbeatPeriod;
                }

                TimeSpan delay = machine.LastSuggestedDelay;
                if (delay >= TimeSpan.FromMilliseconds(1))
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                else
                    await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => StatusText = $"CP-500 error: {ex.Message}");
        }
    }

    private static void AttachCp500Diagnostics(Cp500Machine machine)
    {
        machine.Keyboard.KeyStateChanged += (row, column, pressed) =>
            Cp500DiagnosticTrace.Write($"matrix-state row={row} column={column} pressed={pressed}");
        machine.Keyboard.Matrix.ReadObserved += (rowMask, value) =>
        {
            if (machine.Keyboard.HasPressedKeys || value != byte.MaxValue)
                Cp500DiagnosticTrace.Write($"matrix-read mask={rowMask:X2} value={value:X2}");
        };
    }

    private bool ConsumeCp500InputSettleCycles(int executedCycles)
    {
        int remaining = Volatile.Read(ref _cp500InputSettleCyclesRemaining);
        if (remaining <= 0)
            return true;

        int updated = Interlocked.Add(ref _cp500InputSettleCyclesRemaining, -executedCycles);
        if (updated > 0)
            return false;

        Interlocked.Exchange(ref _cp500InputSettleCyclesRemaining, 0);
        Cp500DiagnosticTrace.Write("input-settle-complete");
        return true;
    }

    private async Task StopCp500Async()
    {
        if (_cp500Cancellation == null)
            return;

        _cp500Cancellation.Cancel();
        try
        {
            if (_cp500RunTask != null)
                await _cp500RunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cp500Cancellation.Dispose();
            _cp500Cancellation = null;
            _cp500RunTask = null;
        }
    }

    public void UpdateDriveStatus(int drive, string? path)
    {
        switch (drive)
        {
            case 0: DriveAPath = path; break;
            case 1: DriveBPath = path; break;
            case 2: DriveCPath = path; break;
            case 3: DriveDPath = path; break;
        }
    }

    public async Task MountAndBoot(int drive)
    {
        if (IsCp500)
            return;

        bool wasRunning = _emulator.IsRunning;
        if (wasRunning)
        {
            await _emulator.StopAsync();
            StopInterruptTimer();
        }

        // If mounting to A: and not running, boot from it
        if (drive == 0 && !wasRunning && _diskProvider.IsMounted(0))
        {
            Span<byte> bootSector = stackalloc byte[128];
            if (_diskProvider.ReadSector(0, 0, 1, bootSector))
            {
                _emulator.Reset();
                _terminal.Buffer.Clear();
                _emulator.Load(0x0000, bootSector);
                _emulator.Cpu.PC = 0x0000;
                _emulator.Cpu.SP = 0xFFFF;
                StartInterruptTimer();
                _emulator.Start();
                StatusText = "Booting...";
                return;
            }
        }

        if (wasRunning)
        {
            StartInterruptTimer();
            _emulator.Start();
        }
    }

    [RelayCommand]
    public void UnmountDisk(int drive)
    {
        _diskProvider.Unmount(drive);
        UpdateDriveStatus(drive, null);
        StatusText = $"Unmounted {(char)('A' + drive)}:";
    }

    public void Dispose()
    {
        StopCp500Async().GetAwaiter().GetResult();
        StopInterruptTimer();
        _emulator.StopAsync().Wait(TimeSpan.FromSeconds(1));
        _emulator.Dispose();

        if (_diskProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
