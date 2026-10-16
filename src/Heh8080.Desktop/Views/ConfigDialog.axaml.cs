using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Heh8080.UI.ViewModels;
using Heh8080.Core;
using Heh8080.Devices;
using Heh8080.Rs232;

namespace Heh8080.Views;

public partial class ConfigDialog : Window
{
    private readonly TextBlock[] _driveNumbers;
    private readonly TextBlock[] _driveLabels;
    private readonly Button[] _mountButtons;
    private readonly Button[] _ejectButtons;
    private readonly (RadioButton Button, double Factor)[] _speeds;
    private MainViewModel? _viewModel;

    public ConfigDialog()
    {
        InitializeComponent();
        _driveNumbers = [DriveAPrefix, DriveBPrefix, DriveCPrefix, DriveDPrefix];
        _driveLabels = [DriveALabel, DriveBLabel, DriveCLabel, DriveDLabel];
        _mountButtons = [MountA, MountB, MountC, MountD];
        _ejectButtons = [EjectA, EjectB, EjectC, EjectD];
        _speeds = [(Speed1x, 1), (Speed2x, 2), (Speed4x, 4), (SpeedMax, double.PositiveInfinity)];
    }

    public void SetViewModel(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        UpdateCpuSelection();
        UpdateMachineSelection();
    }

    private void UpdateMachineSelection()
    {
        if (_viewModel == null) return;
        MachineCpm.IsChecked = _viewModel.ActiveMachine == EmulatedMachine.Cpm;
        MachineCp500.IsChecked = _viewModel.ActiveMachine == EmulatedMachine.Cp500;
        MachineCp500.IsEnabled = _viewModel.IsCp500Available;
        CpuZ80.IsEnabled = !_viewModel.IsCp500;
        Cpu8080.IsEnabled = !_viewModel.IsCp500;
        ResetHint.Text = _viewModel.IsCp500 ? "Restart the CP-500 ROM" : "Warm boot from A:";
        UpdateSpeedSelection();
        UpdateRs232Selection();
        UpdateDriveLabels();
        UpdateHardDiskSelection();
    }

    /// <summary>Sets the checkbox from the live profile. The mount row is enabled only when that flag is on.</summary>
    private void UpdateHardDiskSelection()
    {
        if (_viewModel == null)
            return;

        bool installed = _viewModel.Cp500HardDiskInstalled;
        HardDiskInstalled.IsChecked = installed;
        HardDiskInstalled.IsEnabled = _viewModel.IsCp500;
        HardDiskPrefix.IsEnabled = installed;
        HardDiskLabel.IsEnabled = installed;
        MountHardDiskButton.IsEnabled = installed;
        EjectHardDiskButton.IsEnabled = installed;
        HardDiskLabel.Text = _viewModel.HardDiskName ?? "(empty)";
    }

    private void UpdateRs232Selection()
    {
        if (_viewModel == null)
            return;

        bool enabled = _viewModel.IsCp500;
        Rs232Off.IsEnabled = enabled;
        Rs232Tcp.IsEnabled = enabled;
        Rs232Com.IsEnabled = enabled;
        Rs232Hayes.IsEnabled = enabled;
        Rs232Apply.IsEnabled = enabled;
        ShowRs232Fields();
    }

    private void ShowRs232Fields()
    {
        bool enabled = _viewModel?.IsCp500 == true;
        bool tcp = Rs232Tcp.IsChecked == true;
        Rs232TcpFields.IsVisible = tcp;
        Rs232ComFields.IsVisible = Rs232Com.IsChecked == true;
        Rs232HayesFields.IsVisible = Rs232Hayes.IsChecked == true;
        Rs232Host.IsEnabled = enabled && Rs232Listen.IsChecked != true;
        Rs232Port.IsEnabled = enabled;
        Rs232Listen.IsEnabled = enabled;
        Rs232ComName.IsEnabled = enabled;
        Rs232HayesPort.IsEnabled = enabled;
    }

    /// <summary>CP/M always runs as fast as it can, so the speed choice applies to the CP-500 only.</summary>
    private void UpdateSpeedSelection()
    {
        foreach (var (button, factor) in _speeds)
        {
            button.IsChecked = factor.Equals(_viewModel!.Cp500SpeedFactor);
            button.IsEnabled = _viewModel.IsCp500;
        }
    }

    private void UpdateCpuSelection()
    {
        if (_viewModel == null) return;
        CpuZ80.IsChecked = _viewModel.CpuType == CpuType.ZilogZ80;
        Cpu8080.IsChecked = _viewModel.CpuType == CpuType.Intel8080;
    }

    private void UpdateDriveLabels()
    {
        if (_viewModel == null) return;

        for (int drive = 0; drive < _driveLabels.Length; drive++)
        {
            bool installed = _viewModel.IsDriveInstalled(drive);
            _driveNumbers[drive].Text = _viewModel.IsCp500 ? $"{drive}:" : $"{DriveLetter(drive)}:";
            _driveLabels[drive].Text = installed ? _viewModel.GetDriveLabel(drive) ?? "(empty)" : "(no drive)";
            _mountButtons[drive].IsEnabled = installed;
            _ejectButtons[drive].IsEnabled = installed;
        }
    }

    private static char DriveLetter(int drive) => (char)('A' + drive);

    private async void MountA_Click(object? sender, RoutedEventArgs e) => await MountDisk(0);
    private async void MountB_Click(object? sender, RoutedEventArgs e) => await MountDisk(1);
    private async void MountC_Click(object? sender, RoutedEventArgs e) => await MountDisk(2);
    private async void MountD_Click(object? sender, RoutedEventArgs e) => await MountDisk(3);

    private async void MountHardDisk_Click(object? sender, RoutedEventArgs e) => await MountHardDisk();

    private async void EjectHardDisk_Click(object? sender, RoutedEventArgs e) => await EjectHardDisk();

    private async void HardDisk_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsCp500)
            return;

        try
        {
            await _viewModel.SetCp500HardDisk(HardDiskInstalled.IsChecked == true);
            StatusLabel.Text = _viewModel.StatusText;
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = ex.Message;
            StatusLabel.Text = ex.Message;
        }

        UpdateHardDiskSelection();
    }

    private async Task MountHardDisk()
    {
        if (_viewModel == null || !_viewModel.IsCp500)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Insert Hard Disk Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new("Hard Disk Images") { Patterns = ["*.hdv", "*.img"] },
                new("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0)
            return;

        string path = files[0].Path.LocalPath;
        try
        {
            byte[] image = await File.ReadAllBytesAsync(path);
            bool readOnly = new FileInfo(path).IsReadOnly;
            await _viewModel.MountCp500HardDisk(
                image,
                Path.GetFileName(path),
                saved => SaveAtomically(path, saved),
                readOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel.StatusText = ex.Message;
        }

        UpdateHardDiskSelection();
        StatusLabel.Text = _viewModel.StatusText;
    }

    private async Task EjectHardDisk()
    {
        if (_viewModel == null)
            return;

        await _viewModel.EjectCp500HardDisk();
        UpdateHardDiskSelection();
        StatusLabel.Text = _viewModel.StatusText;
    }

    private async void UnmountA_Click(object? sender, RoutedEventArgs e) => await UnmountDisk(0);
    private async void UnmountB_Click(object? sender, RoutedEventArgs e) => await UnmountDisk(1);
    private async void UnmountC_Click(object? sender, RoutedEventArgs e) => await UnmountDisk(2);
    private async void UnmountD_Click(object? sender, RoutedEventArgs e) => await UnmountDisk(3);

    private async Task MountDisk(int drive)
    {
        if (_viewModel == null) return;

        bool cp500 = _viewModel.IsCp500;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = cp500
                ? $"Insert Disk Image in Drive {drive}"
                : $"Mount Disk Image to Drive {DriveLetter(drive)}:",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                cp500
                    ? new("Disk Images") { Patterns = new[] { "*.dsk", "*.dmk", "*.jv3" } }
                    : new("Disk Images") { Patterns = new[] { "*.img", "*.dsk", "*.cpm" } },
                new("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        if (cp500)
            await MountCp500Disk(drive, path);
        else
            await MountCpmDisk(drive, path);
    }

    private async Task MountCpmDisk(int drive, string path)
    {
        var diskProvider = _viewModel!.DiskProvider as FileDiskImageProvider;
        if (diskProvider == null) return;

        diskProvider.Mount(drive, path, readOnly: false);
        _viewModel.UpdateDriveStatus(drive, Path.GetFileName(path));
        await _viewModel.MountAndBoot(drive);
        UpdateDriveLabels();
        StatusLabel.Text = $"Mounted to {DriveLetter(drive)}:";
    }

    private async Task MountCp500Disk(int drive, string path)
    {
        try
        {
            byte[] image = await File.ReadAllBytesAsync(path);
            bool readOnly = new FileInfo(path).IsReadOnly;
            await _viewModel!.MountCp500Disk(drive, image, Path.GetFileName(path), saved => SaveAtomically(path, saved), readOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewModel!.StatusText = ex.Message;
        }

        UpdateDriveLabels();
        StatusLabel.Text = _viewModel!.StatusText;
    }

    private static void SaveAtomically(string path, byte[] contents)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }

    private async Task UnmountDisk(int drive)
    {
        if (_viewModel == null) return;

        await _viewModel.UnmountDiskCommand.ExecuteAsync(drive);
        UpdateDriveLabels();
        StatusLabel.Text = _viewModel.StatusText;
    }

    private async void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        await _viewModel.ResetCommand.ExecuteAsync(null);
        StatusLabel.Text = "System reset";
    }

    private async void CpuType_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var newType = CpuZ80.IsChecked == true ? CpuType.ZilogZ80 : CpuType.Intel8080;
        if (newType != _viewModel.CpuType)
        {
            await _viewModel.SwitchCpuType(newType);
            StatusLabel.Text = $"Switched to {_viewModel.CpuTypeName}";
        }
    }

    private async void Speed_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        foreach (var (button, factor) in _speeds)
        {
            if (button != sender) continue;

            await _viewModel.SetCp500Speed(factor);
            StatusLabel.Text = _viewModel.StatusText;
        }
    }

    private void Rs232_Click(object? sender, RoutedEventArgs e) => ShowRs232Fields();

    private void Rs232Listen_Click(object? sender, RoutedEventArgs e) => ShowRs232Fields();

    private async void Rs232Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsCp500)
            return;

        try
        {
            Rs232Link link = BuildRs232Link();
            Rs232Attachment cable = Rs232Endpoint.Open(link, Cp500MachineProfile.Original.ClockHz);
            try
            {
                await _viewModel.SetCp500Rs232(cable);
            }
            catch
            {
                cable.Endpoint?.Dispose();
                throw;
            }

            string status = Rs232Status(link);
            _viewModel.StatusText = status;
            StatusLabel.Text = status;
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = ex.Message;
            StatusLabel.Text = ex.Message;
        }
    }

    private Rs232Link BuildRs232Link()
    {
        if (Rs232Off.IsChecked == true)
            return Rs232Link.Off;

        if (Rs232Com.IsChecked == true)
            return Rs232Link.Com(Rs232ComName.Text ?? "");

        int port = Rs232PortNumber(Rs232Hayes.IsChecked == true ? Rs232HayesPort.Text : Rs232Port.Text);
        if (Rs232Hayes.IsChecked == true)
            return Rs232Link.Hayes(port);

        if (Rs232Listen.IsChecked == true)
            return Rs232Link.Listen(port);

        string host = string.IsNullOrWhiteSpace(Rs232Host.Text) ? "127.0.0.1" : Rs232Host.Text.Trim();
        return Rs232Link.Tcp(host, port);
    }

    private static int Rs232PortNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 2323;

        if (!int.TryParse(text, out int port))
            throw new FormatException($"RS-232 link must be {Rs232Link.Syntax}.");

        return port;
    }

    private static string Rs232Status(Rs232Link link) => link switch
    {
        Rs232Link.OffLink => "RS-232 off. Machine restarted.",
        Rs232Link.TcpLink tcp => $"RS-232 TCP {tcp.Host}:{tcp.Port}. Machine restarted.",
        Rs232Link.ListenLink listen => $"RS-232 TCP listen {listen.Port}. Machine restarted.",
        Rs232Link.HayesLink hayes => $"RS-232 Hayes listen {hayes.Port}. Machine restarted.",
        Rs232Link.ComLink com => $"RS-232 COM {com.Name}. Machine restarted.",
        _ => throw new ArgumentOutOfRangeException(nameof(link), link.GetType().Name, "Unknown RS-232 link."),
    };

    private async void Machine_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var profile = MachineCp500.IsChecked == true
            ? EmulatedMachine.Cp500
            : EmulatedMachine.Cpm;
        try
        {
            await _viewModel.SwitchMachine(profile);
            StatusLabel.Text = profile == EmulatedMachine.Cp500
                ? "CP-500 running"
                : "CP/M profile running";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
        UpdateMachineSelection();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
