using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heh8080.Core;
using Heh8080.Devices;
using Heh8080.Terminal;

namespace Heh8080.UI.ViewModels;

public enum EmulatedMachine
{
    Cpm,
    Cp500
}

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private Emulator _emulator;
    private readonly IDiskImageProvider _diskProvider;
    private readonly Adm3aTerminal _terminal;
    private CpuType _cpuType = CpuType.ZilogZ80;
    private readonly byte[]? _cp500Rom;
    private readonly Cp500DriveBay _cp500Drives = new();
    private Cp500Machine? _cp500Machine;
    private ISerialEndpoint? _cp500Serial;
    private OffsetClock? _cp500Clock;
    private CancellationTokenSource? _cp500Cancellation;
    private Task? _cp500RunTask;

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
    public EmulatedMachine ActiveMachine { get; private set; } = EmulatedMachine.Cpm;
    public bool IsCp500 => ActiveMachine == EmulatedMachine.Cp500;
    public bool IsCp500Available => _cp500Rom != null && Cp500Characters != null;

    /// <summary>The character generator the CP-500 display is drawn with, or null when CP-500 is unavailable.</summary>
    public Cp500CharacterGenerator? Cp500Characters { get; }
    public int Cp500InstalledDrives => Cp500MachineProfile.Original.InstalledDrives;
    public Cp500VideoDevice? Cp500Video => _cp500Machine?.Video;
    public Cp500KeyboardDevice? Cp500Keyboard => _cp500Machine?.Keyboard;

    /// <summary>Multiple of real time the CP-500 runs at; <see cref="double.PositiveInfinity"/> is as fast as possible.</summary>
    public double Cp500SpeedFactor { get; private set; } = 1;
    public event Action? ActiveMachineChanged;

    /// <summary>CP-500 needs both user-supplied images: the 16 KiB system ROM and the 2 KiB character generator.</summary>
    public MainViewModel(IDiskImageProvider diskProvider, byte[]? cp500Rom = null, byte[]? cp500CharacterRom = null)
    {
        _diskProvider = diskProvider;
        _cp500Rom = cp500Rom is { Length: > 0 } ? (byte[])cp500Rom.Clone() : null;
        Cp500Characters = cp500CharacterRom is { Length: Cp500CharacterGenerator.ImageSize }
            ? new Cp500CharacterGenerator(cp500CharacterRom)
            : null;

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
            ResetCp500Machine();
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

    public async Task SwitchMachine(EmulatedMachine machine)
    {
        if (machine == ActiveMachine)
            return;

        if (machine == EmulatedMachine.Cp500)
        {
            if (!IsCp500Available)
                throw new InvalidOperationException(
                    "CP-500 ROMs not found. Supply cp500/cp500.rom (16 KiB system ROM) and "
                    + "cp500/cp500-chargen.rom (2 KiB character generator) and restart the app.");

            await _emulator.StopAsync();
            StopInterruptTimer();
            _cp500Machine ??= new Cp500Machine(Cp500MachineProfile.Original, _cp500Rom!, _cp500Drives.Images);
            _cp500Machine.SpeedFactor = Cp500SpeedFactor;
            ResetCp500Machine();
            ActiveMachine = machine;
            ActiveMachineChanged?.Invoke();
            StartCp500();
            StatusText = "CP-500 running";
            return;
        }

        await StopCp500Async();
        _cp500Machine?.Keyboard.ReleaseAll();
        FlushCp500Disks();
        ActiveMachine = machine;
        ActiveMachineChanged?.Invoke();
        await Reset();
    }

    /// <summary>With an empty drive 0 the ROM only waits for a disk; holding BREAK through reset makes it offer BASIC.</summary>
    private void ResetCp500Machine()
    {
        if (_cp500Drives.Images.IsMounted(0))
            _cp500Machine!.Reset();
        else
            _cp500Machine!.ResetHoldingBreak();
    }

    private void StartCp500()
    {
        _cp500Cancellation = new CancellationTokenSource();
        _cp500RunTask = Task.Run(() => RunCp500Async(_cp500Cancellation.Token));
    }

    private async Task RunCp500Async(CancellationToken cancellationToken)
    {
        Cp500Machine machine = _cp500Machine!;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                const int batchCycleLimit = 2048;
                int executedCycles = 0;
                for (; executedCycles < batchCycleLimit && !cancellationToken.IsCancellationRequested;)
                    executedCycles += machine.Step();

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

    /// <summary>Name of the hard disk, or null when the slot is empty. This is not a floppy drive label.</summary>
    public string? HardDiskName => IsCp500 ? _cp500Drives.HardDisk.Name : null;

    /// <summary>FreHD board flag on the running CP-500. Stock and CP/M stay false.</summary>
    public bool Cp500HardDiskInstalled => IsCp500 && _cp500Machine?.Profile.HardDiskInstalled == true;

    /// <summary>Name of the disk in a drive of the active machine, or null when the drive is empty.</summary>
    public string? GetDriveLabel(int drive) => IsCp500
        ? _cp500Drives.NameOf(drive)
        : drive switch
        {
            0 => DriveAPath,
            1 => DriveBPath,
            2 => DriveCPath,
            3 => DriveDPath,
            _ => null
        };

    /// <summary>Whether the active machine has a drive with this number.</summary>
    public bool IsDriveInstalled(int drive) => IsCp500 ? drive < Cp500InstalledDrives : drive is >= 0 and < 4;

    /// <summary>
    /// Inserts a disk image file into a CP-500 drive while the CPU is paused. A disk written to is handed to
    /// <paramref name="writeBack"/> in its source format when it is ejected, replaced, the machine is switched or the app
    /// shuts down, unless it is read-only. Failures are reported through <see cref="StatusText"/>.
    /// </summary>
    public async Task MountCp500Disk(int drive, byte[] image, string name, Action<byte[]>? writeBack, bool readOnly = false)
    {
        try
        {
            await WithCp500Paused(() => _cp500Drives.Insert(drive, image, name, writeBack, readOnly));
            StatusText = $"Inserted {name} in drive {drive}";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not insert {name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Fast-forwards the whole CP-500 (CPU, timer and disk together) by <paramref name="factor"/>, or runs it as fast as
    /// the host allows with <see cref="double.PositiveInfinity"/>. The speed is kept across resets and machine switches.
    /// </summary>
    public async Task SetCp500Speed(double factor)
    {
        if (!(factor > 0))
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The speed factor must be positive.");

        Cp500SpeedFactor = factor;
        if (_cp500Machine != null)
            await WithCp500Paused(() => _cp500Machine.SpeedFactor = factor);
        StatusText = double.IsPositiveInfinity(factor) ? "CP-500 speed: maximum" : $"CP-500 speed: {factor}x";
    }

    /// <summary>
    /// Replaces the CP-500 with a machine on the same disks, RAM, clock, and speed, cabled by <paramref name="rs232"/>.
    /// The hard-disk board stays as it is. The previous cable is released only when the replacement endpoint is a
    /// different object. A failed build leaves the running machine in place and leaves <paramref name="rs232"/> with the caller.
    /// </summary>
    public async Task SetCp500Rs232(Rs232Attachment rs232)
    {
        if (_cp500Machine == null || _cp500Rom == null)
            throw new InvalidOperationException("The CP-500 is not running.");

        await RebuildCp500(rs232, CurrentHardDisk());
    }

    /// <summary>
    /// Rebuilds through <see cref="RebuildCp500"/>. The cable is the live profile flag plus the current endpoint.
    /// The board is installed on the bay slot and the one clock when <paramref name="installed"/> is true.
    /// The slot and its image stay.
    /// </summary>
    public async Task SetCp500HardDisk(bool installed)
    {
        if (_cp500Machine == null || _cp500Rom == null)
            throw new InvalidOperationException("The CP-500 is not running.");

        if (installed)
            _cp500Clock ??= new OffsetClock(() => DateTime.Now);

        var cable = new Rs232Attachment(_cp500Machine.Profile.Rs232Installed, _cp500Serial);
        await RebuildCp500(cable, installed ? InstalledHardDisk() : HardDiskAttachment.None);
        StatusText = installed ? "CP-500 hard disk on" : "CP-500 hard disk off";
    }

    /// <summary>Pauses the CPU, then inserts. A bad image leaves the slot and is reported on <see cref="StatusText"/>.</summary>
    public async Task MountCp500HardDisk(
        byte[] image,
        string name,
        Action<byte[]>? writeBack,
        bool readOnly = false,
        HardDiskGeometry? rawGeometry = null)
    {
        try
        {
            await WithCp500Paused(() => _cp500Drives.HardDisk.Insert(image, name, writeBack, readOnly, rawGeometry));
            StatusText = $"Inserted {name}";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not insert {name}: {ex.Message}";
        }
    }

    /// <summary>Pauses the CPU, then ejects. A failed save leaves the image mounted.</summary>
    public async Task EjectCp500HardDisk()
    {
        try
        {
            await WithCp500Paused(() => _cp500Drives.HardDisk.Eject());
            StatusText = "Ejected hard disk";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save the hard disk, image kept: {ex.Message}";
        }
    }

    /// <summary>
    /// The one machine builder for RS-232 and the hard disk. Applies both attachments.
    /// Disposes the previous serial endpoint only when the replacement endpoint is a different object.
    /// A failed construct leaves the running machine in place.
    /// </summary>
    private async Task RebuildCp500(Rs232Attachment rs232, HardDiskAttachment hardDisk)
    {
        if (_cp500Machine == null || _cp500Rom == null)
            throw new InvalidOperationException("The CP-500 is not running.");

        bool wasRunning = _cp500Cancellation != null;
        await StopCp500Async();
        Cp500Machine previous = _cp500Machine;
        ISerialEndpoint? previousEndpoint = _cp500Serial;
        try
        {
            Cp500MachineProfile profile = hardDisk.Apply(rs232.Apply(new Cp500MachineProfile(
                previous.Profile.RamSizeKiB,
                previous.Profile.InstalledDrives)
            {
                ClockHz = previous.Profile.ClockHz
            }));
            var machine = new Cp500Machine(
                profile, _cp500Rom, _cp500Drives.Images, serial: rs232, hardDisk: hardDisk);
            machine.SpeedFactor = Cp500SpeedFactor;
            _cp500Machine = machine;
            _cp500Serial = rs232.Endpoint;
            ResetCp500Machine();
            if (!ReferenceEquals(previousEndpoint, rs232.Endpoint))
                previousEndpoint?.Dispose();
        }
        catch
        {
            _cp500Machine = previous;
            _cp500Serial = previousEndpoint;
            if (wasRunning)
                StartCp500();
            throw;
        }

        ActiveMachineChanged?.Invoke();
        if (wasRunning)
            StartCp500();
    }

    private HardDiskAttachment CurrentHardDisk() =>
        _cp500Machine?.Profile.HardDiskInstalled == true
            ? InstalledHardDisk()
            : HardDiskAttachment.None;

    private HardDiskAttachment InstalledHardDisk()
    {
        if (_cp500Clock is not { } clock)
            throw new InvalidOperationException("The hard disk has no clock.");

        return HardDiskAttachment.Install(_cp500Drives.HardDisk, clock);
    }

    private async Task WithCp500Paused(Action action)
    {
        bool wasRunning = _cp500Cancellation != null;
        await StopCp500Async();
        try
        {
            action();
        }
        finally
        {
            if (wasRunning)
                StartCp500();
        }
    }

    private bool FlushCp500Disks()
    {
        try
        {
            _cp500Drives.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save a disk: {ex.Message}";
            return false;
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
    public async Task UnmountDisk(int drive)
    {
        if (IsCp500)
        {
            await EjectCp500Disk(drive);
            return;
        }

        _diskProvider.Unmount(drive);
        UpdateDriveStatus(drive, null);
        StatusText = $"Unmounted {(char)('A' + drive)}:";
    }

    private async Task EjectCp500Disk(int drive)
    {
        try
        {
            await WithCp500Paused(() => _cp500Drives.Eject(drive));
            StatusText = $"Ejected drive {drive}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not save drive {drive}, disk kept: {ex.Message}";
        }
    }

    public void Dispose()
    {
        StopCp500Async().GetAwaiter().GetResult();
        _cp500Serial?.Dispose();
        _cp500Serial = null;
        FlushCp500Disks();
        StopInterruptTimer();
        _emulator.StopAsync().Wait(TimeSpan.FromSeconds(1));
        _emulator.Dispose();

        if (_diskProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
