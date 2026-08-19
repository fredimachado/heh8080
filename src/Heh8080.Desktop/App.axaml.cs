using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Heh8080.UI.ViewModels;
using Heh8080.UI.Diagnostics;
using Heh8080.Devices;
using Heh8080.Views;

namespace Heh8080;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private string? _extractedDiskPath;
    private static readonly object Cp500DiagnosticsLock = new();
    private static StreamWriter? _cp500Diagnostics;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ConfigureCp500Diagnostics();
            var diskProvider = new FileDiskImageProvider();
            _viewModel = new MainViewModel(diskProvider, LoadCp500Rom());

            desktop.MainWindow = new MainWindow
            {
                DataContext = _viewModel
            };

            // Clean up on shutdown
            desktop.ShutdownRequested += (s, e) =>
            {
                _viewModel?.Dispose();
                DisposeCp500Diagnostics();
                CleanupTempDisk();
            };

            if (desktop.Args.Contains("--cp500", StringComparer.OrdinalIgnoreCase))
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await _viewModel.SwitchMachineProfile(DesktopMachineProfile.Cp500);
                    }
                    catch (Exception ex)
                    {
                        _viewModel.StatusText = $"CP-500 startup failed: {ex.Message}";
                    }
                });
            }
            else
            {
                // Auto-boot from bundled LOLOS disk
                AutoBoot(diskProvider);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureCp500Diagnostics()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "heh8080");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "cp500-diagnostics.log");
            _cp500Diagnostics = new StreamWriter(path, append: false) { AutoFlush = true };
            Cp500DiagnosticTrace.Configure(message =>
            {
                lock (Cp500DiagnosticsLock)
                    _cp500Diagnostics?.WriteLine(message);
            });
            Cp500DiagnosticTrace.Write($"trace-start path={path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CP-500 diagnostics unavailable: {ex.Message}");
        }
    }

    private static void DisposeCp500Diagnostics()
    {
        Cp500DiagnosticTrace.Configure(null);
        lock (Cp500DiagnosticsLock)
        {
            _cp500Diagnostics?.Dispose();
            _cp500Diagnostics = null;
        }
    }

    private void AutoBoot(FileDiskImageProvider diskProvider)
    {
        try
        {
            // Try to extract and mount bundled LOLOS disk
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

                // Boot from drive A:
                _viewModel!.Boot();
            }
            else
            {
                // No bundled disk - just show welcome
                _viewModel!.Boot();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Auto-boot failed: {ex.Message}");
            _viewModel?.Boot();
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

    private static byte[]? LoadCp500Rom()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                string path = Path.Combine(directory.FullName, "cp500", "CP500-1.BIN");
                if (File.Exists(path))
                    return File.ReadAllBytes(path);

                directory = directory.Parent;
            }
        }

        return null;
    }
}
