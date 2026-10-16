using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Heh8080.Cp500Runner;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500RunnerTests : IDisposable
{
    private const string BlankRows = "\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n";
    private readonly string _directory = Directory.CreateTempSubdirectory("heh8080-cp500-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void UntilScreenSucceedsWhenTheTextAppearsAndDumpScreenPrintsTheScreen()
    {
        string rom = File(Cp500TestRoms.Printing("HELLO, CP-500"));

        var result = Run("--rom", rom, "--until-screen", "CP-500", "--dump-screen");

        Assert.Equal(RunnerExitCode.Success, result.ExitCode);
        Assert.Equal("HELLO, CP-500" + BlankRows + "\n", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public void UntilScreenTimesOutWithExitCodeOneAndShowsTheScreenOnStderr()
    {
        string rom = File(Cp500TestRoms.Printing("WAITING"));

        var result = Run("--rom", rom, "--run", "100ms", "--until-screen", "DONE", "--timeout", "500ms");

        Assert.Equal(RunnerExitCode.ScreenTimeout, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal(
            "heh8080-cp500: step 2 (--until-screen \"DONE\") timed out after 0.5s of emulated time, at 0.6s. Screen:\n" +
            "WAITING" + BlankRows + "\n",
            result.Stderr);
    }

    [Fact]
    public void StepsRunInCommandLineOrder()
    {
        string rom = File(Cp500TestRoms.PrintingAfterAKey("GOT KEY"));

        var typedFirst = Run("--rom", rom, "--type", "a", "--until-screen", "GOT KEY", "--timeout", "1s");
        var waitedFirst = Run("--rom", rom, "--until-screen", "GOT KEY", "--type", "a", "--timeout", "1s");

        Assert.Equal(RunnerExitCode.Success, typedFirst.ExitCode);
        Assert.Equal(RunnerExitCode.ScreenTimeout, waitedFirst.ExitCode);
    }

    [Fact]
    public void TypePressesTheMappedSwitchesWithShift()
    {
        string rom = File(Cp500TestRoms.KeyboardLatch());

        var result = Run("--rom", rom, "--type", "Ga", "--dump-memory", "3C00:2");

        Assert.Equal(RunnerExitCode.Success, result.ExitCode);
        Assert.Equal("3C00: 82 01\n", result.Stdout);
    }

    [Theory]
    [InlineData(ModelIIIDiskImageKind.Raw, "boot.dsk")]
    [InlineData(ModelIIIDiskImageKind.Jv3, "boot.jv3")]
    [InlineData(ModelIIIDiskImageKind.Dmk, "boot.dmk")]
    public void DisksOfEveryFormatBootAndDumpMemoryShowsWhatTheRomRead(ModelIIIDiskImageKind kind, string name)
    {
        string rom = File(Cp500TestRoms.ReadingBootSector());
        string disk = File(Disk(kind, "BOOTDATA"), name);

        var result = Run("--rom", rom, "--disk0", disk, "--run", "100ms", "--dump-memory", "4300:8", "--dump-memory", "0x3FFF:1");

        Assert.Equal(RunnerExitCode.Success, result.ExitCode);
        Assert.Equal("4300: 42 4F 4F 54 44 41 54 41\n3FFF: 00\n", result.Stdout);
    }

    [Fact]
    public void DumpMemoryPrintsSixteenBytesPerLine()
    {
        string rom = File(Cp500TestRoms.Printing("0123456789ABCDEFGH"));

        var result = Run("--rom", rom, "--run", "50ms", "--dump-memory", "3C00h:18");

        Assert.Equal(
            "3C00: 30 31 32 33 34 35 36 37 38 39 41 42 43 44 45 46\n3C10: 47 48\n",
            result.Stdout);
    }

    [Theory]
    [InlineData(ModelIIIDiskImageKind.Raw, "data.dsk")]
    [InlineData(ModelIIIDiskImageKind.Dmk, "data.dmk")]
    public void WriteBackSavesModifiedDisksInTheirOwnFormat(ModelIIIDiskImageKind kind, string name)
    {
        string writer = File(Cp500TestRoms.WritingBootSector(Sector("WRITTEN!")), "writer.rom");
        string reader = File(Cp500TestRoms.ReadingBootSector(), "reader.rom");
        string disk = File(Disk(kind, "ORIGINAL"), name);

        Assert.Equal(RunnerExitCode.Success, Run("--rom", writer, "--disk0", disk, "--run", "100ms").ExitCode);
        var untouched = Run("--rom", reader, "--disk0", disk, "--run", "100ms", "--dump-memory", "4300:8");
        Assert.Equal(RunnerExitCode.Success, Run("--rom", writer, "--disk0", disk, "--write-back", "--run", "100ms").ExitCode);
        var saved = Run("--rom", reader, "--disk0", disk, "--run", "100ms", "--dump-memory", "4300:8");

        Assert.Equal("4300: 4F 52 49 47 49 4E 41 4C\n", untouched.Stdout);
        Assert.Equal("4300: 57 52 49 54 54 45 4E 21\n", saved.Stdout);
        Assert.Equal(ModelIIIDiskCodec.Serialize(ModelIIIDiskCodec.Parse(System.IO.File.ReadAllBytes(disk), kind), kind).Length,
            new FileInfo(disk).Length);
        Assert.False(System.IO.File.Exists(disk + ".tmp"));
    }

    [Fact]
    public void ListenInstallsTheBoardTheRomCanRead()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        string rom = File([0xDB, 0xE8, 0x32, 0x00, 0x40, 0x18, 0xFE]);
        var result = Run("--rom", rom, "--rs232", $"listen:{port}", "--run", "50ms", "--dump-memory", "4000:1");

        Assert.Equal(RunnerExitCode.Success, result.ExitCode);
        Assert.Contains("4000: 02", result.Stdout);
    }

    [Theory]
    [InlineData(new string[0], "--rom is required.")]
    [InlineData(new[] { "--dump-screen" }, "--rom is required.")]
    [InlineData(new[] { "--rom" }, "--rom needs a value.")]
    [InlineData(new[] { "--rom", "a", "--rom", "b" }, "--rom was given more than once.")]
    [InlineData(new[] { "--rom", "a", "--disk1", "x", "--disk1", "y" }, "--disk1 was given more than once.")]
    [InlineData(new[] { "--rom", "a", "--disk4", "x" }, "Unknown option '--disk4'.")]
    [InlineData(new[] { "--rom", "a", "--fast" }, "Unknown option '--fast'.")]
    [InlineData(new[] { "--rom", "a", "boot.dsk" }, "Unexpected argument 'boot.dsk'.")]
    [InlineData(new[] { "--rom", "a", "--timeout", "30" }, "--timeout needs a duration such as 500ms, 30s or 2m, not '30'.")]
    [InlineData(new[] { "--rom", "a", "--run", "1h" }, "--run needs a duration such as 500ms, 30s or 2m, not '1h'.")]
    [InlineData(new[] { "--rom", "a", "--run", "0s" }, "--run must be more than zero and at most a day, not '0s'.")]
    [InlineData(new[] { "--rom", "a", "--timeout", "1s", "--timeout", "2s" }, "--timeout was given more than once.")]
    [InlineData(new[] { "--rom", "a", "--until-screen", "" }, "--until-screen needs non-empty text.")]
    [InlineData(new[] { "--rom", "a", "--type", "a^" }, "--type \"a^\": The CP-500 keyboard has no key for '^' (position 1).")]
    [InlineData(new[] { "--rom", "a", "--type", "{F1}" }, "--type \"{F1}\": Unknown key '{F1}' at position 0; known keys: ENTER, CLEAR, BREAK, UP, DOWN, LEFT, RIGHT, SPACE.")]
    [InlineData(new[] { "--rom", "a", "--dump-memory", "4300" }, "--dump-memory needs <hex address>:<length>, such as 4300:256, not '4300'.")]
    [InlineData(new[] { "--rom", "a", "--dump-memory", "FFFF:2" }, "--dump-memory 'FFFF:2': the length must be 1 to 1 bytes from FFFFh.")]
    [InlineData(new[] { "--rom", "a", "--rs232", "tcp:a:b:1" }, "RS-232 link must be off | tcp:<host>:<port> | listen:<port> | hayes:<port> | com:<name>.")]
    [InlineData(new[] { "--rom", "a", "--rs232", "off", "--rs232", "listen:1" }, "--rs232 was given more than once.")]
    public void UsageErrorsExitWithTwoAndSayWhatIsWrong(string[] args, string message)
    {
        var result = Run(args);

        Assert.Equal(RunnerExitCode.UsageError, result.ExitCode);
        Assert.Equal($"heh8080-cp500: {message}\nRun 'heh8080-cp500 --help' for usage.\n", result.Stderr);
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public void MissingFilesAndInvalidImagesExitWithTwo()
    {
        string rom = File(Cp500TestRoms.Spinning());
        string missing = Path.Combine(_directory, "missing.rom");
        string empty = File([], "empty.rom");
        string garbage = File(new byte[1000], "garbage.dmk");

        var noRom = Run("--rom", missing);
        var emptyRom = Run("--rom", empty);
        var badDisk = Run("--rom", rom, "--disk2", garbage);

        Assert.Equal(RunnerExitCode.UsageError, noRom.ExitCode);
        Assert.StartsWith($"heh8080-cp500: Cannot read ROM '{missing}': ", noRom.Stderr);
        Assert.Equal(RunnerExitCode.UsageError, emptyRom.ExitCode);
        Assert.Equal($"heh8080-cp500: ROM '{empty}' is empty.\n", emptyRom.Stderr);
        Assert.Equal(RunnerExitCode.UsageError, badDisk.ExitCode);
        Assert.StartsWith($"heh8080-cp500: disk2 '{garbage}' is not a valid disk image: ", badDisk.Stderr);
    }

    [Fact]
    public void HelpPrintsUsageAndSucceeds()
    {
        var result = Run("--help");

        Assert.Equal(RunnerExitCode.Success, result.ExitCode);
        Assert.StartsWith("Usage: heh8080-cp500 --rom <path> [options] [steps]\n", result.Stdout);
    }

    [Fact]
    public void TheExecutablePrintsTheScreenAsUtf8AndReturnsTheExitCode()
    {
        string rom = File(Cp500TestRoms.Printing("ol\u0003 \u0081\u0095\u00BF"));
        string runner = typeof(RunnerCommand).Assembly.Location;

        var (exitCode, stdout) = RunProcess("dotnet", runner, "--rom", rom, "--until-screen", "olé", "--dump-screen");
        var (timeoutCode, _) = RunProcess("dotnet", runner, "--rom", rom, "--until-screen", "nope", "--timeout", "100ms");

        Assert.Equal(0, exitCode);
        Assert.Equal("olé \U0001FB00▌█" + BlankRows + "\n", stdout);
        Assert.Equal(1, timeoutCode);
    }

    [RealRomFact]
    public void RealRomBootsToBasicTypesALineAndSeesTheAnswer()
    {
        var result = Run(
            "--rom", Cp500RomFile.Path!,
            "--until-screen", "BASIC (S ou N)",
            "--type", @"S\r\r",
            "--until-screen", "READY",
            "--type", @"PRINT 2+3\r",
            "--until-screen", " 5",
            "--dump-screen");

        Assert.Equal(RunnerExitCode.Success, result.ExitCode);
        Assert.Contains("PROLOGICA CP-500", result.Stdout);
        Assert.Contains("\n>PRINT 2+3\n 5\nREADY\n>\n", result.Stdout);
    }

    private (RunnerExitCode ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        RunnerExitCode exitCode = RunnerCommand.Execute(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static (int ExitCode, string Stdout) RunProcess(string fileName, params string[] args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false
        };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _ = stderr.Result;
        return (process.ExitCode, stdout);
    }

    private string File(byte[] contents, string name = "test.rom")
    {
        string path = Path.Combine(_directory, name);
        System.IO.File.WriteAllBytes(path, contents);
        return path;
    }

    private static byte[] Disk(ModelIIIDiskImageKind kind, string bootSectorText)
    {
        byte[] raw = new byte[ModelIIIDiskGeometry.DiskSizeBytes];
        Sector(bootSectorText).CopyTo(raw, 0);
        return kind == ModelIIIDiskImageKind.Raw
            ? raw
            : ModelIIIDiskCodec.Serialize(ModelIIIDiskCodec.Parse(raw, ModelIIIDiskImageKind.Raw), kind);
    }

    private static byte[] Sector(string text)
    {
        byte[] sector = new byte[256];
        Encoding.ASCII.GetBytes(text).CopyTo(sector, 0);
        return sector;
    }
}
