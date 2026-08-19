namespace Heh8080.Devices;

/// <summary>Supported CP-500 hardware families.</summary>
public enum Cp500Revision
{
    /// <summary>The original 2.03 MHz CP-500.</summary>
    Original,

    /// <summary>The CP-500/M80 with SO-08-compatible low-RAM overlay support.</summary>
    M80,

    /// <summary>The compact CP-500/M80C without cassette hardware.</summary>
    M80C,

    /// <summary>The 4 MHz CP-500 Turbo.</summary>
    Turbo
}

/// <summary>
/// Immutable hardware configuration used to build a <see cref="Cp500Machine"/>.
/// </summary>
public sealed record Cp500MachineProfile
{
    /// <summary>Frequency of the original Z80 clock.</summary>
    public const long OriginalClockHz = 2_030_000;

    /// <summary>Frequency of the CP-500 Turbo Z80 clock.</summary>
    public const long TurboClockHz = 4_000_000;

    /// <summary>Size of the system ROM socket; keyboard and video overlay part of this range.</summary>
    public const int SystemRomSizeBytes = 16 * 1024;

    /// <summary>Creates a hardware profile.</summary>
    public Cp500MachineProfile(Cp500Revision revision = Cp500Revision.Original, int ramSizeKiB = 48)
    {
        Revision = revision;
        ValidateRamSize(revision, ramSizeKiB);
        RamSizeKiB = ramSizeKiB;
    }

    /// <summary>Original 48 KiB CP-500 configuration.</summary>
    public static Cp500MachineProfile Original { get; } = new();

    /// <summary>64 KiB M80 configuration with a switchable boot ROM.</summary>
    public static Cp500MachineProfile M80 { get; } = new(Cp500Revision.M80, 64);

    /// <summary>Configured hardware revision.</summary>
    public Cp500Revision Revision { get; }

    /// <summary>Installed RAM in KiB.</summary>
    public int RamSizeKiB { get; }

    /// <summary>Installed RAM in bytes.</summary>
    public int RamSizeBytes => RamSizeKiB * 1024;

    /// <summary>Z80 clock rate used by <see cref="Heh8080.Core.CycleThrottle"/>.</summary>
    public long ClockHz => Revision == Cp500Revision.Turbo ? TurboClockHz : OriginalClockHz;

    /// <summary>
    /// Whether firmware can expose underlying RAM at reset-ROM addresses.
    /// A switchable low-RAM overlay requires both an M80-family board and 64 KiB installed.
    /// </summary>
    public bool SupportsRomOverlay =>
        (Revision is Cp500Revision.M80 or Cp500Revision.M80C) && RamSizeKiB == 64;

    /// <summary>Whether the physical cassette interface is present.</summary>
    public bool HasCassettePort => Revision is Cp500Revision.Original or Cp500Revision.M80;

    private static void ValidateRamSize(Cp500Revision revision, int ramSizeKiB)
    {
        bool valid = revision switch
        {
            Cp500Revision.Original or Cp500Revision.Turbo => ramSizeKiB is 16 or 32 or 48,
            Cp500Revision.M80 or Cp500Revision.M80C => ramSizeKiB is 16 or 32 or 48 or 64,
            _ => false
        };

        if (!valid)
            throw new ArgumentOutOfRangeException(
                nameof(ramSizeKiB),
                "CP-500 RAM must be 16, 32, or 48 KiB; M80 variants also support 64 KiB.");
    }
}
