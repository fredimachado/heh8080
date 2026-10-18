using Heh8080.Devices;

namespace Heh8080.Cp500Runner;

/// <summary>A step of the run script, executed in command-line order.</summary>
internal abstract record RunnerStep(string Option);

/// <summary>Run until the text appears on screen, or fail when the wait timeout passes.</summary>
internal sealed record UntilScreenStep(string Text) : RunnerStep("--until-screen");

/// <summary>Type the keys through the keyboard matrix.</summary>
internal sealed record TypeStep(string Text, IReadOnlyList<Cp500KeyPress> Keys) : RunnerStep("--type");

/// <summary>Run for a fixed emulated duration.</summary>
internal sealed record RunStep(TimeSpan Duration) : RunnerStep("--run");

/// <summary>A disk image file for a drive.</summary>
internal sealed record DiskOption(int Drive, string Path);

/// <summary>A range of the 64 KiB address space to print at exit; it never runs past FFFFh.</summary>
internal sealed record MemoryRange
{
    public MemoryRange(ushort address, int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 0x10000 - address);
        Address = address;
        Length = length;
    }

    public ushort Address { get; }

    public int Length { get; }
}

/// <summary>A command line that parsed into something to do.</summary>
internal abstract record ParsedCommandLine;

/// <summary>Print the usage text.</summary>
internal sealed record ShowHelp : ParsedCommandLine;

/// <summary>Boot the machine and run the steps.</summary>
internal sealed record RunMachine(
    string RomPath,
    IReadOnlyList<DiskOption> Disks,
    string? HardDiskPath,
    bool HardDiskHalt,
    bool W5100,
    bool WriteBack,
    TimeSpan Timeout,
    bool DumpScreen,
    IReadOnlyList<MemoryRange> DumpMemory,
    IReadOnlyList<RunnerStep> Steps) : ParsedCommandLine;
