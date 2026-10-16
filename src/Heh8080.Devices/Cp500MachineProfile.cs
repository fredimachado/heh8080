namespace Heh8080.Devices;

/// <summary>
/// Immutable hardware configuration used to build a <see cref="Cp500Machine"/>.
/// </summary>
public sealed record Cp500MachineProfile
{
    /// <summary>Size of the system ROM socket; keyboard and video overlay part of this range.</summary>
    public const int SystemRomSizeBytes = 16 * 1024;

    /// <summary>Creates a hardware profile with 16, 32, or 48 KiB of RAM and zero to four installed disk drives.</summary>
    public Cp500MachineProfile(int ramSizeKiB = 48, int installedDrives = 2)
    {
        if (ramSizeKiB is not (16 or 32 or 48))
            throw new ArgumentOutOfRangeException(nameof(ramSizeKiB), "CP-500 RAM must be 16, 32, or 48 KiB.");

        if (installedDrives is < 0 or > ModelIIIDiskGeometry.MaxDrives)
            throw new ArgumentOutOfRangeException(
                nameof(installedDrives), $"CP-500 supports 0 to {ModelIIIDiskGeometry.MaxDrives} disk drives.");

        RamSizeKiB = ramSizeKiB;
        InstalledDrives = installedDrives;
    }

    /// <summary>Original 48 KiB CP-500 configuration.</summary>
    public static Cp500MachineProfile Original { get; } = new();

    /// <summary>Installed RAM in KiB.</summary>
    public int RamSizeKiB { get; }

    /// <summary>Installed RAM in bytes.</summary>
    public int RamSizeBytes => RamSizeKiB * 1024;

    /// <summary>Installed disk drives, numbered from zero. The CP-500 ships with two internal drives; zero is a cassette-only machine.</summary>
    public int InstalledDrives { get; }

    /// <summary>Z80 clock rate used by <see cref="Heh8080.Core.CycleThrottle"/> and the 30 Hz timer.</summary>
    public long ClockHz { get; init; } = 2_030_000;

    /// <summary>
    /// Model III RS-232 board. Stock machines leave this false, so E8h-EBh stay unregistered and read FFh.
    /// <see cref="Rs232Attachment.Apply"/> is the production writer.
    /// </summary>
    public bool Rs232Installed { get; init; }
}
