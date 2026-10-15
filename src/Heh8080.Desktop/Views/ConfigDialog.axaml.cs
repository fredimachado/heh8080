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

namespace Heh8080.Views;

public partial class ConfigDialog : Window
{
    private readonly TextBlock[] _driveNumbers;
    private readonly TextBlock[] _driveLabels;
    private readonly Button[] _mountButtons;
    private readonly Button[] _ejectButtons;
    private MainViewModel? _viewModel;

    public ConfigDialog()
    {
        InitializeComponent();
        _driveNumbers = [DriveAPrefix, DriveBPrefix, DriveCPrefix, DriveDPrefix];
        _driveLabels = [DriveALabel, DriveBLabel, DriveCLabel, DriveDLabel];
        _mountButtons = [MountA, MountB, MountC, MountD];
        _ejectButtons = [EjectA, EjectB, EjectC, EjectD];
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
        UpdateDriveLabels();
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
