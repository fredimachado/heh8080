namespace Heh8080.Devices;

/// <summary>Physical side count for a Model III-compatible disk image.</summary>
public enum ModelIIIDiskFormat
{
    /// <summary>180 KiB: 40 tracks, one side, 18 sectors, and 256 bytes per sector.</summary>
    SingleSided,

    /// <summary>360 KiB: 40 tracks, two sides, 18 sectors, and 256 bytes per sector.</summary>
    DoubleSided
}

/// <summary>Host-independent disk-file encodings accepted by the Model III codec layer.</summary>
public enum ModelIIIDiskImageKind
{
    /// <summary>Flat 18-sector-per-track 256-byte-sector media.</summary>
    Raw,

    /// <summary>JV1 sector stream.</summary>
    Jv1,

    /// <summary>JV3 metadata header plus sector payload stream.</summary>
    Jv3,

    /// <summary>DMK track container.</summary>
    Dmk
}

/// <summary>Per-sector density flag retained for native image round-trips.</summary>
public enum ModelIIIDiskDensity
{
    /// <summary>Single-density FM sector.</summary>
    SingleDensity,

    /// <summary>Double-density MFM sector.</summary>
    DoubleDensity
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

    /// <summary>Bytes transferred by a normal WD1791 sector command.</summary>
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

    /// <summary>Returns the canonical offset in a flat raw image.</summary>
    public static int GetRawOffset(int track, int side, int sector, int sideCount) =>
        (((track * sideCount + side) * SectorsPerTrack) + (sector - 1)) * BytesPerSector;
}

/// <summary>Native sector record used by WD1791 commands and image codecs.</summary>
public sealed record ModelIIIDiskSector(
    int Track,
    int Side,
    int Id,
    byte[] Data,
    ModelIIIDiskDensity Density = ModelIIIDiskDensity.SingleDensity,
    bool DeletedData = false,
    bool CrcValid = true)
{
    /// <summary>WD17xx size code for the payload length.</summary>
    public byte SizeCode => Data.Length switch
    {
        128 => 0,
        256 => 1,
        512 => 2,
        1024 => 3,
        _ => 1
    };
}

/// <summary>Metadata returned by READ ADDRESS without copying the sector payload.</summary>
public readonly record struct ModelIIIDiskSectorHeader(
    int Track,
    int Side,
    int Id,
    byte SizeCode,
    ModelIIIDiskDensity Density,
    bool DeletedData,
    bool CrcValid);

/// <summary>Decoded host-independent Model III disk image.</summary>
public sealed class ModelIIIDiskImage
{
    private readonly List<ModelIIIDiskSector> _sectors;

    /// <summary>Creates a native image from sector records.</summary>
    public ModelIIIDiskImage(
        ModelIIIDiskFormat format,
        IEnumerable<ModelIIIDiskSector> sectors,
        ModelIIIDiskImageKind sourceKind = ModelIIIDiskImageKind.Raw)
    {
        Format = format;
        SourceKind = sourceKind;
        _sectors = sectors.Select(CloneSector).ToList();
        Validate();
    }

    /// <summary>Physical format inferred or declared for this image.</summary>
    public ModelIIIDiskFormat Format { get; }

    /// <summary>Original host container format.</summary>
    public ModelIIIDiskImageKind SourceKind { get; }

    /// <summary>Native sectors in host-independent form.</summary>
    public IReadOnlyList<ModelIIIDiskSector> Sectors => _sectors;

    /// <summary>Finds a sector by physical address.</summary>
    public ModelIIIDiskSector? FindSector(int track, int side, int id) =>
        _sectors.FirstOrDefault(sector => sector.Track == track && sector.Side == side && sector.Id == id);

    /// <summary>Returns the first sector header for a track/side.</summary>
    public bool TryGetFirstHeader(int track, int side, out ModelIIIDiskSectorHeader header)
    {
        var sector = _sectors.FirstOrDefault(value => value.Track == track && value.Side == side);
        if (sector is null)
        {
            header = default;
            return false;
        }

        header = ToHeader(sector);
        return true;
    }

    /// <summary>Creates a flat erased image.</summary>
    public static ModelIIIDiskImage CreateBlank(ModelIIIDiskFormat format)
    {
        var sectors = new List<ModelIIIDiskSector>();
        int sides = ModelIIIDiskGeometry.GetSideCount(format);
        for (int track = 0; track < ModelIIIDiskGeometry.Tracks; track++)
        for (int side = 0; side < sides; side++)
        for (int id = 1; id <= ModelIIIDiskGeometry.SectorsPerTrack; id++)
        {
            byte[] data = new byte[ModelIIIDiskGeometry.BytesPerSector];
            Array.Fill(data, (byte)0xE5);
            sectors.Add(new ModelIIIDiskSector(track, side, id, data));
        }

        return new ModelIIIDiskImage(format, sectors);
    }

    /// <summary>Creates a native image from a flat raw byte stream, padding short input with E5h.</summary>
    public static ModelIIIDiskImage FromRaw(ReadOnlySpan<byte> image, ModelIIIDiskFormat format)
    {
        byte[] raw = new byte[ModelIIIDiskGeometry.GetDiskSizeBytes(format)];
        Array.Fill(raw, (byte)0xE5);
        image[..Math.Min(image.Length, raw.Length)].CopyTo(raw);

        var sectors = new List<ModelIIIDiskSector>();
        int sides = ModelIIIDiskGeometry.GetSideCount(format);
        for (int track = 0; track < ModelIIIDiskGeometry.Tracks; track++)
        for (int side = 0; side < sides; side++)
        for (int id = 1; id <= ModelIIIDiskGeometry.SectorsPerTrack; id++)
        {
            int offset = ModelIIIDiskGeometry.GetRawOffset(track, side, id, sides);
            sectors.Add(new ModelIIIDiskSector(track, side, id, raw.AsSpan(offset, ModelIIIDiskGeometry.BytesPerSector).ToArray()));
        }

        return new ModelIIIDiskImage(format, sectors);
    }

    /// <summary>Serializes the image as a flat raw byte stream.</summary>
    public byte[] ToRawBytes()
    {
        int sides = ModelIIIDiskGeometry.GetSideCount(Format);
        byte[] raw = new byte[ModelIIIDiskGeometry.GetDiskSizeBytes(Format)];
        Array.Fill(raw, (byte)0xE5);
        foreach (var sector in _sectors)
        {
            if (!ModelIIIDiskGeometry.IsValidSector(sector.Track, sector.Side, sector.Id, sides))
                continue;

            int offset = ModelIIIDiskGeometry.GetRawOffset(sector.Track, sector.Side, sector.Id, sides);
            sector.Data.AsSpan(0, Math.Min(ModelIIIDiskGeometry.BytesPerSector, sector.Data.Length))
                .CopyTo(raw.AsSpan(offset, ModelIIIDiskGeometry.BytesPerSector));
        }

        return raw;
    }

    private void Validate()
    {
        int sides = ModelIIIDiskGeometry.GetSideCount(Format);
        var seen = new HashSet<(int Track, int Side, int Id)>();
        foreach (var sector in _sectors)
        {
            if (!ModelIIIDiskGeometry.IsValidSector(sector.Track, sector.Side, sector.Id, sides))
                throw new InvalidDataException($"Invalid Model III sector T{sector.Track} S{sector.Side} ID{sector.Id}.");
            if (sector.Data.Length is not (128 or 256 or 512 or 1024))
                throw new InvalidDataException("Model III sector payloads must be 128, 256, 512, or 1024 bytes.");
            if (!seen.Add((sector.Track, sector.Side, sector.Id)))
                throw new InvalidDataException($"Duplicate Model III sector T{sector.Track} S{sector.Side} ID{sector.Id}.");
        }
    }

    private static ModelIIIDiskSector CloneSector(ModelIIIDiskSector sector) =>
        sector with { Data = sector.Data.ToArray() };

    internal static ModelIIIDiskSectorHeader ToHeader(ModelIIIDiskSector sector) =>
        new(sector.Track, sector.Side, sector.Id, sector.SizeCode, sector.Density, sector.DeletedData, sector.CrcValid);
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

    /// <summary>Returns native metadata for a sector when the provider has it.</summary>
    bool TryGetSectorHeader(int drive, int track, int side, int sector, out ModelIIIDiskSectorHeader header)
    {
        header = default;
        return false;
    }

    /// <summary>Reads a serialized view of one track for READ TRACK.</summary>
    bool ReadTrack(int drive, int track, int side, Span<byte> buffer)
    {
        if (buffer.Length < ModelIIIDiskGeometry.SectorsPerTrack * ModelIIIDiskGeometry.BytesPerSector)
            return false;

        for (int sector = 1; sector <= ModelIIIDiskGeometry.SectorsPerTrack; sector++)
        {
            if (!ReadSector(
                    drive,
                    track,
                    side,
                    sector,
                    buffer.Slice((sector - 1) * ModelIIIDiskGeometry.BytesPerSector, ModelIIIDiskGeometry.BytesPerSector)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes a serialized track from WRITE TRACK.</summary>
    bool WriteTrack(int drive, int track, int side, ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ModelIIIDiskGeometry.SectorsPerTrack * ModelIIIDiskGeometry.BytesPerSector)
            return false;

        for (int sector = 1; sector <= ModelIIIDiskGeometry.SectorsPerTrack; sector++)
        {
            if (!WriteSector(
                    drive,
                    track,
                    side,
                    sector,
                    buffer.Slice((sector - 1) * ModelIIIDiskGeometry.BytesPerSector, ModelIIIDiskGeometry.BytesPerSector)))
            {
                return false;
            }
        }

        return true;
    }
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
        => Mount(drive, ModelIIIDiskImage.FromRaw(image, format), readOnly);

    /// <summary>Mounts a native decoded image.</summary>
    public void Mount(int drive, ModelIIIDiskImage image, bool readOnly = false)
    {
        ValidateDrive(drive);
        ArgumentNullException.ThrowIfNull(image);
        _images[drive] = new MountedImage(image, readOnly);
    }

    /// <summary>Mounts a blank 180 KiB single-sided erased image.</summary>
    public void MountBlank(int drive, bool readOnly = false) => MountBlank(drive, ModelIIIDiskFormat.SingleSided, readOnly);

    /// <summary>Mounts a blank erased image in the declared physical format.</summary>
    public void MountBlank(int drive, ModelIIIDiskFormat format, bool readOnly = false) =>
        Mount(drive, ModelIIIDiskImage.CreateBlank(format), readOnly);

    /// <summary>Unmounts a drive.</summary>
    public void Unmount(int drive)
    {
        ValidateDrive(drive);
        _images[drive] = null;
    }

    /// <summary>Returns a raw snapshot of the mounted image.</summary>
    public byte[] GetRawImage(int drive)
    {
        ValidateDrive(drive);
        return _images[drive]?.Image.ToRawBytes() ?? throw new InvalidOperationException("No image is mounted.");
    }

    public bool IsMounted(int drive) => IsValidDrive(drive) && _images[drive] is not null;

    public bool IsReadOnly(int drive) =>
        IsValidDrive(drive) && _images[drive] is { ReadOnly: true };

    public bool ReadSector(int drive, int track, int side, int sector, Span<byte> buffer)
    {
        if (!TryGetSector(drive, track, side, sector, out var native) ||
            buffer.Length < ModelIIIDiskGeometry.BytesPerSector)
        {
            return false;
        }

        native.Data.AsSpan(0, Math.Min(native.Data.Length, ModelIIIDiskGeometry.BytesPerSector))
            .CopyTo(buffer[..ModelIIIDiskGeometry.BytesPerSector]);
        return native.CrcValid;
    }

    public bool WriteSector(int drive, int track, int side, int sector, ReadOnlySpan<byte> buffer)
    {
        if (IsReadOnly(drive) ||
            !TryGetSector(drive, track, side, sector, out var native) ||
            buffer.Length < ModelIIIDiskGeometry.BytesPerSector)
        {
            return false;
        }

        buffer[..ModelIIIDiskGeometry.BytesPerSector].CopyTo(native.Data);
        return true;
    }

    public bool TryGetSectorHeader(int drive, int track, int side, int sector, out ModelIIIDiskSectorHeader header)
    {
        if (!TryGetSector(drive, track, side, sector, out var native))
        {
            header = default;
            return false;
        }

        header = ModelIIIDiskImage.ToHeader(native);
        return true;
    }

    public bool ReadTrack(int drive, int track, int side, Span<byte> buffer)
    {
        if (!IsValidDrive(drive) || _images[drive] is not { } mounted)
            return false;

        int offset = 0;
        foreach (var sector in mounted.Image.Sectors.Where(sector => sector.Track == track && sector.Side == side).OrderBy(sector => sector.Id))
        {
            if (offset + sector.Data.Length > buffer.Length)
                return false;

            sector.Data.CopyTo(buffer[offset..]);
            offset += sector.Data.Length;
        }

        return offset > 0;
    }

    public bool WriteTrack(int drive, int track, int side, ReadOnlySpan<byte> buffer)
    {
        if (IsReadOnly(drive) || !IsValidDrive(drive) || _images[drive] is not { } mounted)
            return false;

        int offset = 0;
        foreach (var sector in mounted.Image.Sectors.Where(sector => sector.Track == track && sector.Side == side).OrderBy(sector => sector.Id))
        {
            if (offset + sector.Data.Length > buffer.Length)
                return false;

            buffer.Slice(offset, sector.Data.Length).CopyTo(sector.Data);
            offset += sector.Data.Length;
        }

        return offset > 0;
    }

    private bool TryGetSector(int drive, int track, int side, int sector, out ModelIIIDiskSector native)
    {
        native = null!;
        if (!IsValidDrive(drive) || _images[drive] is not { } mounted)
            return false;

        native = mounted.Image.FindSector(track, side, sector)!;
        return native is not null;
    }

    private static bool IsValidDrive(int drive) => drive is >= 0 and < MaxDrives;

    private static void ValidateDrive(int drive)
    {
        if (!IsValidDrive(drive))
            throw new ArgumentOutOfRangeException(nameof(drive));
    }

    private sealed record MountedImage(ModelIIIDiskImage Image, bool ReadOnly);
}
