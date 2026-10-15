using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Heh8080.UI.ViewModels;
using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Views;

public partial class ConfigDialog : Window
{
    private MainViewModel? _viewModel;

    public ConfigDialog()
    {
        InitializeComponent();
    }

    public void SetViewModel(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        UpdateDriveLabels();
        UpdateCpuSelection();
        UpdateMachineSelection();
    }

    private void UpdateMachineSelection()
    {
        if (_viewModel == null) return;
        MachineCpm.IsChecked = _viewModel.MachineProfile == DesktopMachineProfile.Cpm;
        MachineCp500.IsChecked = _viewModel.MachineProfile == DesktopMachineProfile.Cp500;
        MachineCp500.IsEnabled = _viewModel.IsCp500Available;
        CpuZ80.IsEnabled = !_viewModel.IsCp500;
        Cpu8080.IsEnabled = !_viewModel.IsCp500;
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

        DriveALabel.Text = _viewModel.DriveAPath ?? "(empty)";
        DriveBLabel.Text = _viewModel.DriveBPath ?? "(empty)";
        DriveCLabel.Text = _viewModel.DriveCPath ?? "(empty)";
        DriveDLabel.Text = _viewModel.DriveDPath ?? "(empty)";
    }

    private async void MountA_Click(object? sender, RoutedEventArgs e) => await MountDisk(0);
    private async void MountB_Click(object? sender, RoutedEventArgs e) => await MountDisk(1);
    private async void MountC_Click(object? sender, RoutedEventArgs e) => await MountDisk(2);
    private async void MountD_Click(object? sender, RoutedEventArgs e) => await MountDisk(3);

    private void UnmountA_Click(object? sender, RoutedEventArgs e) => UnmountDisk(0);
    private void UnmountB_Click(object? sender, RoutedEventArgs e) => UnmountDisk(1);
    private void UnmountC_Click(object? sender, RoutedEventArgs e) => UnmountDisk(2);
    private void UnmountD_Click(object? sender, RoutedEventArgs e) => UnmountDisk(3);

    private async System.Threading.Tasks.Task MountDisk(int drive)
    {
        if (_viewModel == null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Mount Disk Image to Drive {(char)('A' + drive)}:",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Disk Images") { Patterns = new[] { "*.img", "*.dsk", "*.cpm" } },
                new("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].Path.LocalPath;
            var diskProvider = _viewModel.DiskProvider as FileDiskImageProvider;
            if (diskProvider != null)
            {
                diskProvider.Mount(drive, path, readOnly: false);
                _viewModel.UpdateDriveStatus(drive, Path.GetFileName(path));
                await _viewModel.MountAndBoot(drive);
                UpdateDriveLabels();
                StatusLabel.Text = $"Mounted to {(char)('A' + drive)}:";
            }
        }
    }

    private void UnmountDisk(int drive)
    {
        if (_viewModel == null) return;

        _viewModel.UnmountDiskCommand.Execute(drive);
        UpdateDriveLabels();
        StatusLabel.Text = $"Ejected {(char)('A' + drive)}:";
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

    private async void MachineProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var profile = MachineCp500.IsChecked == true
            ? DesktopMachineProfile.Cp500
            : DesktopMachineProfile.Cpm;
        try
        {
            await _viewModel.SwitchMachineProfile(profile);
            StatusLabel.Text = profile == DesktopMachineProfile.Cp500
                ? "CP-500: cp500/cp500.rom"
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
