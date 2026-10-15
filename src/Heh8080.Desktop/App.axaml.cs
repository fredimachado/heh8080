using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Heh8080.UI.ViewModels;
using Heh8080.Devices;
using Heh8080.Views;

namespace Heh8080;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private string? _extractedDiskPath;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var diskProvider = new FileDiskImageProvider();
            _viewModel = new MainViewModel(
                diskProvider, LoadCp500File("cp500.rom"), LoadCp500File("cp500-chargen.rom"));

            desktop.MainWindow = new MainWindow
            {
                DataContext = _viewModel
            };

            // Clean up on shutdown
            desktop.ShutdownRequested += (s, e) =>
            {
                _viewModel?.Dispose();
                CleanupTempDisk();
            };

            MountBundledDisk(diskProvider);

            if (desktop.Args.Contains("--cp500", StringComparer.OrdinalIgnoreCase))
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await _viewModel.SwitchMachine(EmulatedMachine.Cp500);
                    }
                    catch (Exception ex)
                    {
                        _viewModel.StatusText = $"CP-500 startup failed: {ex.Message}";
                    }
                });
            }
            else
            {
                _viewModel.Boot();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void MountBundledDisk(FileDiskImageProvider diskProvider)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Heh8080.Desktop.Assets.disks.lolos.dsk";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                // Extract to temp file
                _extractedDiskPath = Path.Combine(Path.GetTempPath(), "heh8080_lolos.dsk");
                using (var fs = File.Create(_extractedDiskPath))
                {
                    stream.CopyTo(fs);
                }

                // Mount to drive A:
                diskProvider.Mount(0, _extractedDiskPath, readOnly: false);
                _viewModel!.UpdateDriveStatus(0, "lolos.img");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Mounting the bundled disk failed: {ex.Message}");
        }
    }

    private void CleanupTempDisk()
    {
        if (_extractedDiskPath != null && File.Exists(_extractedDiskPath))
        {
            try { File.Delete(_extractedDiskPath); }
            catch { /* Ignore cleanup errors */ }
        }
    }

    private static byte[]? LoadCp500File(string fileName)
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "cp500", fileName),
            Path.Combine(Environment.CurrentDirectory, "cp500", fileName),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "heh8080",
                fileName)
        };

        string? path = candidates.FirstOrDefault(File.Exists);
        return path == null ? null : File.ReadAllBytes(path);
    }
}