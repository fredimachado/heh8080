namespace Heh8080.Devices;

/// <summary>
/// Pairs the profile flag with the bay slot and the clock.
/// <see cref="Apply"/> is the only production writer of <see cref="Cp500MachineProfile.HardDiskInstalled"/>.
/// The slot is the bay's <see cref="Cp500DriveBay.HardDisk"/>. A second slot would make Flush save the wrong image.
/// </summary>
public readonly record struct HardDiskAttachment(bool Installed, HardDiskSlot? Media, IClock? Clock)
{
    /// <summary>Board absent. The constructor registers no controller.</summary>
    public static HardDiskAttachment None { get; } = new(false, null, null);

    /// <summary>Rejects a null slot or clock. The result has <see cref="Installed"/> true.</summary>
    public static HardDiskAttachment Install(HardDiskSlot media, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(clock);
        return new(true, media, clock);
    }

    /// <summary>Returns a copy of <paramref name="profile"/> whose hard-disk flag equals <see cref="Installed"/>.</summary>
    public Cp500MachineProfile Apply(Cp500MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile with { HardDiskInstalled = Installed };
    }
}
