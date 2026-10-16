using System.Diagnostics;
using System.Globalization;
using System.Text;
using Heh8080.Devices;

namespace Heh8080.Cp500Runner;

/// <summary>Process exit codes of <c>heh8080-cp500</c>.</summary>
public enum RunnerExitCode
{
    /// <summary>Every step succeeded.</summary>
    Success = 0,

    /// <summary>An <c>--until-screen</c> step timed out.</summary>
    ScreenTimeout = 1,

    /// <summary>Bad arguments, a missing or unreadable file, or an invalid ROM or disk image.</summary>
    UsageError = 2
}

/// <summary>
/// The <c>heh8080-cp500</c> command: parses the arguments, loads the files, runs the steps on a
/// <see cref="Cp500Automation"/> and prints the requested dumps. All machine behavior lives in Devices.
/// </summary>
public static class RunnerCommand
{
    private const string Name = "heh8080-cp500";
    private const int DumpBytesPerLine = 16;

    /// <summary>Runs the command and returns its exit code. Dumps go to <paramref name="stdout"/>, diagnostics to <paramref name="stderr"/>.</summary>
    public static RunnerExitCode Execute(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        try
        {
            switch (CommandLine.Parse(args))
            {
                case ShowHelp:
                    stdout.WriteLine(CommandLine.Usage);
                    return RunnerExitCode.Success;
                case RunMachine run:
                    return Run(run, stdout, stderr);
                default:
                    throw new UnreachableException("Unknown command line kind.");
            }
        }
        catch (UsageException error)
        {
            stderr.WriteLine($"{Name}: {error.Message}");
            stderr.WriteLine($"Run '{Name} --help' for usage.");
            return RunnerExitCode.UsageError;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            stderr.WriteLine($"{Name}: {error.Message}");
            return RunnerExitCode.UsageError;
        }
    }

    private static RunnerExitCode Run(RunMachine run, TextWriter stdout, TextWriter stderr)
    {
        Cp500Automation automation = Boot(run);
        RunnerExitCode result = RunSteps(automation, run, stderr);

        automation.Drives.Flush();
        if (run.DumpScreen)
            stdout.WriteLine(automation.ReadScreenText());
        foreach (MemoryRange range in run.DumpMemory)
            WriteHexDump(stdout, range.Address, automation.ReadMemory(range.Address, range.Length));

        return result;
    }

    private static Cp500Automation Boot(RunMachine run)
    {
        byte[] rom = ReadFile("ROM", run.RomPath);
        if (rom.Length == 0)
            throw new InvalidDataException($"ROM '{run.RomPath}' is empty.");

        var drives = new Cp500DriveBay();
        foreach (DiskOption disk in run.Disks)
        {
            byte[] image = ReadFile($"disk{disk.Drive}", disk.Path);
            bool readOnly = File.GetAttributes(disk.Path).HasFlag(FileAttributes.ReadOnly);
            Action<byte[]>? writeBack = run.WriteBack ? bytes => WriteAtomically(disk.Path, bytes) : null;
            try
            {
                drives.Insert(disk.Drive, image, Path.GetFileName(disk.Path), writeBack, readOnly);
            }
            catch (InvalidDataException error)
            {
                throw new InvalidDataException($"disk{disk.Drive} '{disk.Path}' is not a valid disk image: {error.Message}", error);
            }
        }

        return new Cp500Automation(rom, drives);
    }

    private static RunnerExitCode RunSteps(Cp500Automation automation, RunMachine run, TextWriter stderr)
    {
        for (int index = 0; index < run.Steps.Count; index++)
        {
            switch (run.Steps[index])
            {
                case UntilScreenStep wait:
                    if (!automation.RunUntilScreenContains(wait.Text, run.Timeout))
                    {
                        stderr.WriteLine(
                            $"{Name}: step {index + 1} ({wait.Option} \"{wait.Text}\") timed out after " +
                            $"{Seconds(run.Timeout)} of emulated time, at {Seconds(automation.Elapsed)}. Screen:");
                        stderr.WriteLine(automation.ReadScreenText());
                        return RunnerExitCode.ScreenTimeout;
                    }

                    break;
                case TypeStep type:
                    automation.Type(type.Keys);
                    break;
                case RunStep step:
                    automation.Run(step.Duration);
                    break;
                default:
                    throw new UnreachableException($"Unknown step {run.Steps[index]}.");
            }
        }

        return RunnerExitCode.Success;
    }

    private static byte[] ReadFile(string role, string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot read {role} '{path}': {error.Message}", error);
        }
    }

    /// <summary>Writes beside the file and renames over it, so an interrupted save never leaves half an image.</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    private static void WriteHexDump(TextWriter writer, ushort start, byte[] bytes)
    {
        var line = new StringBuilder();
        for (int offset = 0; offset < bytes.Length; offset += DumpBytesPerLine)
        {
            line.Clear().Append(CultureInfo.InvariantCulture, $"{start + offset:X4}:");
            for (int i = offset; i < Math.Min(bytes.Length, offset + DumpBytesPerLine); i++)
                line.Append(CultureInfo.InvariantCulture, $" {bytes[i]:X2}");

            writer.WriteLine(line);
        }
    }

    private static string Seconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
}
