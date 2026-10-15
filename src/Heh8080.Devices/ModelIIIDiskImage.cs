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

    /// <summary>Number of drives the controller can address.</summary>
    public const int MaxDrives = 4;

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

/// <summary>
/// Native sector record used by WD1791 commands and image codecs.
/// <see cref="Track"/> and <see cref="Side"/> locate the sector physically;
/// <see cref="IdTrack"/> is the track number recorded in its ID field when that differs.
/// </summary>
public sealed record ModelIIIDiskSector(
    int Track,
    int Side,
    int Id,
    byte[] Data,
    ModelIIIDiskDensity Density = ModelIIIDiskDensity.DoubleDensity,
    bool DeletedData = false,
    bool CrcValid = true,
    int? IdTrack = null)
{
    /// <summary>Track number recorded in the sector ID field.</summary>
    public int RecordedTrack => IdTrack ?? Track;

    /// <summary>WD17xx size code for the payload length.</summary>
    public byte SizeCode => Data.Length switch
    {
        128 => 0,
        256 => 1,
        512 => 2,
        1024 => 3,
        _ => throw new InvalidOperationException("Model III sector payloads must be 128, 256, 512, or 1024 bytes.")
    };
}

/// <summary>Decoded host-independent Model III disk image.</summary>
public sealed class ModelIIIDiskImage
{
    private readonly Dictionary<(int Track, int Side), List<ModelIIIDiskSector>> _tracks = [];

    /// <summary>Creates a native image from sector records, keeping their order within each track.</summary>
    public ModelIIIDiskImage(
        ModelIIIDiskFormat format,
        IEnumerable<ModelIIIDiskSector> sectors,
        ModelIIIDiskImageKind sourceKind = ModelIIIDiskImageKind.Raw,
        bool writeProtected = false)
    {
        Format = format;
        SourceKind = sourceKind;
        WriteProtected = writeProtected;

        foreach (var sector in sectors)
        {
            if (FindProblem(sector) is { } problem)
                throw new InvalidDataException(problem);

            var track = GetOrAddTrack(sector.Track, sector.Side);
            if (track.Any(existing => existing.Id == sector.Id))
                throw new InvalidDataException($"Duplicate Model III sector T{sector.Track} S{sector.Side} ID{sector.Id}.");

            track.Add(CloneSector(sector));
        }
    }

    /// <summary>Physical format inferred or declared for this image.</summary>
    public ModelIIIDiskFormat Format { get; }

    /// <summary>Original host container format.</summary>
    public ModelIIIDiskImageKind SourceKind { get; }

    /// <summary>Whether the source container asked for the disk to be treated as write protected.</summary>
    public bool WriteProtected { get; }

    /// <summary>Native sectors in host-independent form, ordered by track, side, then rotational position.</summary>
    public IReadOnlyList<ModelIIIDiskSector> Sectors =>
        _tracks.OrderBy(entry => entry.Key.Track).ThenBy(entry => entry.Key.Side).SelectMany(entry => entry.Value).ToList();

    /// <summary>Returns the sectors of a physical track in rotational order. The list is live and must not be modified.</summary>
    public IReadOnlyList<ModelIIIDiskSector> GetTrack(int track, int side) =>
        _tracks.TryGetValue((track, side), out var sectors) ? sectors : [];

    /// <summary>Finds a sector by physical address.</summary>
    public ModelIIIDiskSector? FindSector(int track, int side, int id) =>
        GetTrack(track, side).FirstOrDefault(sector => sector.Id == id);

    /// <summary>Overwrites the payload of an existing sector, clearing any CRC error.</summary>
    public bool WriteSector(int track, int side, int id, ReadOnlySpan<byte> data, bool deletedData)
    {
        if (!_tracks.TryGetValue((track, side), out var sectors))
            return false;

        int index = sectors.FindIndex(sector => sector.Id == id);
        if (index < 0 || sectors[index].Data.Length != data.Length)
            return false;

        sectors[index] = sectors[index] with { Data = data.ToArray(), DeletedData = deletedData, CrcValid = true };
        return true;
    }

    /// <summary>Replaces every sector of a physical track, for example after a WRITE TRACK format.</summary>
    public bool TryReplaceTrack(int track, int side, IReadOnlyList<ModelIIIDiskSector> sectors)
    {
        var ids = new HashSet<int>();
        bool valid = sectors.All(sector =>
            sector.Track == track && sector.Side == side && FindProblem(sector) is null && ids.Add(sector.Id));
        if (!valid || side >= ModelIIIDiskGeometry.GetSideCount(Format))
            return false;

        if (sectors.Count == 0)
            _tracks.Remove((track, side));
        else
            _tracks[(track, side)] = sectors.Select(CloneSector).ToList();

        return true;
    }

    /// <summary>Creates a flat erased double-density image.</summary>
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
        foreach (var sector in Sectors)
        {
            if (!ModelIIIDiskGeometry.IsValidSector(sector.Track, sector.Side, sector.Id, sides))
                continue;

            int offset = ModelIIIDiskGeometry.GetRawOffset(sector.Track, sector.Side, sector.Id, sides);
            sector.Data.AsSpan(0, Math.Min(ModelIIIDiskGeometry.BytesPerSector, sector.Data.Length))
                .CopyTo(raw.AsSpan(offset, ModelIIIDiskGeometry.BytesPerSector));
        }

        return raw;
    }

    private List<ModelIIIDiskSector> GetOrAddTrack(int track, int side)
    {
        if (!_tracks.TryGetValue((track, side), out var sectors))
            _tracks[(track, side)] = sectors = [];

        return sectors;
    }

    private string? FindProblem(ModelIIIDiskSector sector)
    {
        if (sector.Track is < 0 or >= ModelIIIDiskGeometry.Tracks ||
            sector.Side < 0 || sector.Side >= ModelIIIDiskGeometry.GetSideCount(Format) ||
            sector.Id is < 0 or > byte.MaxValue)
        {
            return $"Invalid Model III sector T{sector.Track} S{sector.Side} ID{sector.Id}.";
        }

        return sector.Data.Length is 128 or 256 or 512 or 1024
            ? null
            : "Model III sector payloads must be 128, 256, 512, or 1024 bytes.";
    }

    private static ModelIIIDiskSector CloneSector(ModelIIIDiskSector sector) =>
        sector with { Data = sector.Data.ToArray() };
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

    /// <summary>
    /// Returns the sectors of a physical track in rotational order, or an empty list when
    /// the drive is empty or the track does not exist. Callers must not modify the result.
    /// </summary>
    IReadOnlyList<ModelIIIDiskSector> GetTrack(int drive, int track, int side);

    /// <summary>Overwrites the payload of an existing sector, optionally marking it with a deleted data mark.</summary>
    bool WriteSector(int drive, int track, int side, int sector, ReadOnlySpan<byte> data, bool deletedData = false);

    /// <summary>Replaces every sector of a track with the sectors a format operation produced.</summary>
    bool ReplaceTrack(int drive, int track, int side, IReadOnlyList<ModelIIIDiskSector> sectors);
}

/// <summary>
/// In-memory Model III disk-image provider useful for browser hosts and deterministic tests.
/// </summary>
public sealed class MemoryModelIIIDiskImageProvider : IModelIIIDiskImageProvider
{
    private readonly MountedImage?[] _images = new MountedImage?[ModelIIIDiskGeometry.MaxDrives];

    /// <summary>Mounts a defensive 180 KiB single-sided image, padding short media with <c>E5h</c>.</summary>
    public void Mount(int drive, ReadOnlySpan<byte> image, bool readOnly = false)
        => Mount(drive, image, ModelIIIDiskFormat.SingleSided, readOnly);

    /// <summary>
    /// Mounts a defensive copy in the declared physical format, padding short media with <c>E5h</c>.
    /// The format is explicit so a 180 KiB image is never addressed as a double-sided image.
    /// </summary>
    public void Mount(int drive, ReadOnlySpan<byte> image, ModelIIIDiskFormat format, bool readOnly = false)
        => Mount(drive, ModelIIIDiskImage.FromRaw(image, format), readOnly);

    /// <summary>Parses and mounts an image stored in a host container format.</summary>
    public void Mount(int drive, ReadOnlySpan<byte> image, ModelIIIDiskImageKind kind, bool readOnly = false)
        => Mount(drive, ModelIIIDiskCodec.Parse(image, kind), readOnly);

    /// <summary>Mounts a native decoded image. A write-protected source container always mounts read-only.</summary>
    public void Mount(int drive, ModelIIIDiskImage image, bool readOnly = false)
    {
        ValidateDrive(drive);
        ArgumentNullException.ThrowIfNull(image);
        _images[drive] = new MountedImage(image, readOnly || image.WriteProtected);
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

    /// <summary>Returns a snapshot of the mounted image in the container format it was mounted from.</summary>
    public byte[] GetImageBytes(int drive)
    {
        var image = GetMounted(drive).Image;
        return ModelIIIDiskCodec.Serialize(image, image.SourceKind);
    }

    public bool IsMounted(int drive) => IsValidDrive(drive) && _images[drive] is not null;

    public bool IsReadOnly(int drive) =>
        IsValidDrive(drive) && _images[drive] is { ReadOnly: true };

    public IReadOnlyList<ModelIIIDiskSector> GetTrack(int drive, int track, int side) =>
        IsValidDrive(drive) && _images[drive] is { } mounted ? mounted.Image.GetTrack(track, side) : [];

    public bool WriteSector(int drive, int track, int side, int sector, ReadOnlySpan<byte> data, bool deletedData = false) =>
        TryGetWritable(drive, out var mounted) &&
        mounted.RecordChange(mounted.Image.WriteSector(track, side, sector, data, deletedData));

    public bool ReplaceTrack(int drive, int track, int side, IReadOnlyList<ModelIIIDiskSector> sectors) =>
        TryGetWritable(drive, out var mounted) &&
        mounted.RecordChange(mounted.Image.TryReplaceTrack(track, side, sectors));

    /// <summary>Whether the mounted image changed since it was mounted or last marked saved.</summary>
    public bool IsModified(int drive) => IsValidDrive(drive) && _images[drive] is { Modified: true };

    /// <summary>Records that the mounted image was persisted, so <see cref="IsModified"/> reports false until the next write.</summary>
    public void MarkSaved(int drive) => GetMounted(drive).Modified = false;

    private bool TryGetWritable(int drive, out MountedImage mounted)
    {
        mounted = null!;
        if (!IsValidDrive(drive) || _images[drive] is not { ReadOnly: false } writable)
            return false;

        mounted = writable;
        return true;
    }

    private MountedImage GetMounted(int drive)
    {
        ValidateDrive(drive);
        return _images[drive] ?? throw new InvalidOperationException("No image is mounted.");
    }

    private static bool IsValidDrive(int drive) => drive is >= 0 and < ModelIIIDiskGeometry.MaxDrives;

    private static void ValidateDrive(int drive)
    {
        if (!IsValidDrive(drive))
            throw new ArgumentOutOfRangeException(nameof(drive));
    }

    private sealed class MountedImage(ModelIIIDiskImage image, bool readOnly)
    {
        public ModelIIIDiskImage Image { get; } = image;

        public bool ReadOnly { get; } = readOnly;

        public bool Modified { get; set; }

        public bool RecordChange(bool changed)
        {
            Modified |= changed;
            return changed;
        }
    }
}
