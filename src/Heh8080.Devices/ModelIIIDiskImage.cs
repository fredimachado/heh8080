namespace Heh8080.Devices;

/// <summary>Physical side count for a Model III-compatible disk image.</summary>
public enum ModelIIIDiskFormat
{
    /// <summary>180 KiB: 40 tracks, one side, 18 sectors, and 256 bytes per sector.</summary>
    SingleSided,

    /// <summary>360 KiB: 40 tracks, two sides, 18 sectors, and 256 bytes per sector.</summary>
    DoubleSided
}

/// <summary>Physical geometry used by CP-500 / TRS-80 Model III floppy disks.</summary>
public static class ModelIIIDiskGeometry
{
    /// <summary>Number of physical tracks.</summary>
    public const int Tracks = 40;

    /// <summary>Number of sides in the default 180 KiB image format.</summary>
    public const int Sides = 1;

    /// <summary>Maximum number of sides supported by the optional 360 KiB image format.</summary>
    public const int MaximumSides = 2;

    /// <summary>Number of one-based sectors per track.</summary>
    public const int SectorsPerTrack = 18;

    /// <summary>Bytes transferred by a WD1791 sector command.</summary>
    public const int BytesPerSector = 256;

    /// <summary>Total capacity of the default 180 KiB single-sided disk image.</summary>
    public const int DiskSizeBytes = Tracks * Sides * SectorsPerTrack * BytesPerSector;

    /// <summary>Total capacity of an optional 360 KiB double-sided disk image.</summary>
    public const int DoubleSidedDiskSizeBytes = Tracks * MaximumSides * SectorsPerTrack * BytesPerSector;

    /// <summary>Returns the physical side count for a mounted image format.</summary>
    public static int GetSideCount(ModelIIIDiskFormat format) => format switch
    {
        ModelIIIDiskFormat.SingleSided => Sides,
        ModelIIIDiskFormat.DoubleSided => MaximumSides,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>Returns the image capacity for a mounted image format.</summary>
    public static int GetDiskSizeBytes(ModelIIIDiskFormat format) => format switch
    {
        ModelIIIDiskFormat.SingleSided => DiskSizeBytes,
        ModelIIIDiskFormat.DoubleSided => DoubleSidedDiskSizeBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>Returns whether a physical track, side, and one-based sector are valid.</summary>
    public static bool IsValidSector(int track, int side, int sector, int sideCount = Sides) =>
        track >= 0 && track < Tracks &&
        side >= 0 && side < sideCount &&
        sector >= 1 && sector <= SectorsPerTrack;
}

/// <summary>
/// Storage contract for a Model III sector device. Images remain owned by the host;
/// this abstraction intentionally has no filesystem or browser dependency.
/// </summary>
public interface IModelIIIDiskImageProvider
{
    /// <summary>Whether a drive contains an image.</summary>
    bool IsMounted(int drive);

    /// <summary>Whether a mounted drive accepts writes.</summary>
    bool IsReadOnly(int drive);

    /// <summary>Reads one 256-byte physical sector.</summary>
    bool ReadSector(int drive, int track, int side, int sector, Span<byte> buffer);

    /// <summary>Writes one 256-byte physical sector.</summary>
    bool WriteSector(int drive, int track, int side, int sector, ReadOnlySpan<byte> buffer);
}

/// <summary>
/// In-memory Model III disk-image provider useful for browser hosts and deterministic tests.
/// </summary>
public sealed class MemoryModelIIIDiskImageProvider : IModelIIIDiskImageProvider
{
    /// <summary>Maximum conventional drive count supported by this provider.</summary>
    public const int MaxDrives = 4;

    private readonly MountedImage?[] _images = new MountedImage?[MaxDrives];

    /// <summary>Mounts a defensive 180 KiB single-sided image, padding short media with <c>E5h</c>.</summary>
    public void Mount(int drive, ReadOnlySpan<byte> image, bool readOnly = false)
        => Mount(drive, image, ModelIIIDiskFormat.SingleSided, readOnly);

    /// <summary>
    /// Mounts a defensive copy in the declared physical format, padding short media with <c>E5h</c>.
    /// The format is explicit so a 180 KiB image is never addressed as a double-sided image.
    /// </summary>
    public void Mount(int drive, ReadOnlySpan<byte> image, ModelIIIDiskFormat format, bool readOnly = false)
    {
        ValidateDrive(drive);

        byte[] mounted = new byte[ModelIIIDiskGeometry.GetDiskSizeBytes(format)];
        Array.Fill(mounted, (byte)0xE5);
        image[..Math.Min(image.Length, mounted.Length)].CopyTo(mounted);

        _images[drive] = new MountedImage(mounted, format, readOnly);
    }

    /// <summary>Mounts a blank 180 KiB single-sided erased image.</summary>
    public void MountBlank(int drive, bool readOnly = false) => Mount(drive, ReadOnlySpan<byte>.Empty, readOnly);

    /// <summary>Mounts a blank erased image in the declared physical format.</summary>
    public void MountBlank(int drive, ModelIIIDiskFormat format, bool readOnly = false) =>
        Mount(drive, ReadOnlySpan<byte>.Empty, format, readOnly);

    /// <summary>Unmounts a drive.</summary>
    public void Unmount(int drive)
    {
        ValidateDrive(drive);
        _images[drive] = null;
    }

    public bool IsMounted(int drive) => IsValidDrive(drive) && _images[drive] is not null;

    public bool IsReadOnly(int drive) =>
        IsValidDrive(drive) && _images[drive] is { ReadOnly: true };

    public bool ReadSector(int drive, int track, int side, int sector, Span<byte> buffer)
    {
        if (!TryGetOffset(drive, track, side, sector, buffer.Length, out int offset))
            return false;

        _images[drive]!.Data.AsSpan(offset, ModelIIIDiskGeometry.BytesPerSector).CopyTo(buffer);
        return true;
    }

    public bool WriteSector(int drive, int track, int side, int sector, ReadOnlySpan<byte> buffer)
    {
        if (IsReadOnly(drive) ||
            !TryGetOffset(drive, track, side, sector, buffer.Length, out int offset))
        {
            return false;
        }

        buffer[..ModelIIIDiskGeometry.BytesPerSector].CopyTo(
            _images[drive]!.Data.AsSpan(offset, ModelIIIDiskGeometry.BytesPerSector));
        return true;
    }

    private bool TryGetOffset(int drive, int track, int side, int sector, int bufferLength, out int offset)
    {
        offset = 0;
        if (!IsValidDrive(drive) ||
            _images[drive] is not { } mounted ||
            bufferLength < ModelIIIDiskGeometry.BytesPerSector ||
            !ModelIIIDiskGeometry.IsValidSector(
                track,
                side,
                sector,
                ModelIIIDiskGeometry.GetSideCount(mounted.Format)))
        {
            return false;
        }

        offset = (((track * ModelIIIDiskGeometry.GetSideCount(mounted.Format) + side) *
                   ModelIIIDiskGeometry.SectorsPerTrack) +
                  (sector - 1)) * ModelIIIDiskGeometry.BytesPerSector;
        return true;
    }

    private static bool IsValidDrive(int drive) => drive is >= 0 and < MaxDrives;

    private static void ValidateDrive(int drive)
    {
        if (!IsValidDrive(drive))
            throw new ArgumentOutOfRangeException(nameof(drive));
    }

    private sealed record MountedImage(byte[] Data, ModelIIIDiskFormat Format, bool ReadOnly);
}
