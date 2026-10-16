namespace Heh8080.Devices;

/// <summary>
/// Cylinder, head, and sector counts for one image. Sector numbers are 0-based.
/// The guest can address cylinders 0 through 1023, heads 0 through 7, and sectors 0 through 255.
/// </summary>
public readonly record struct HardDiskGeometry
{
    /// <summary>Every sector this board transfers.</summary>
    public const int BytesPerSector = 256;

    /// <summary>Ten-bit cylinder register. Counts are 1 through 1023.</summary>
    public const int MaxCylinders = 1023;

    /// <summary>FreHD head field. Counts are 1 through 8.</summary>
    public const int MaxHeads = 8;

    /// <summary>One-byte sector number. 256 sectors are numbered 0 through 255.</summary>
    public const int MaxSectorsPerTrack = 256;

    /// <summary>Rejects counts outside 1..1023 cylinders, 1..8 heads, and 1..256 sectors per track.</summary>
    public HardDiskGeometry(int cylinders, int heads, int sectorsPerTrack)
    {
        if (cylinders is < 1 or > MaxCylinders)
            throw new ArgumentOutOfRangeException(nameof(cylinders));

        if (heads is < 1 or > MaxHeads)
            throw new ArgumentOutOfRangeException(nameof(heads));

        if (sectorsPerTrack is < 1 or > MaxSectorsPerTrack)
            throw new ArgumentOutOfRangeException(nameof(sectorsPerTrack));

        Cylinders = cylinders;
        Heads = heads;
        SectorsPerTrack = sectorsPerTrack;
    }

    /// <summary>Cylinder count.</summary>
    public int Cylinders { get; }

    /// <summary>Head count.</summary>
    public int Heads { get; }

    /// <summary>Sectors on one track. Sector 0 is the first.</summary>
    public int SectorsPerTrack { get; }

    /// <summary>Sectors in the image, in cylinder-head-sector order.</summary>
    public int SectorCount => Cylinders * Heads * SectorsPerTrack;

    /// <summary>True when the address is inside this geometry. Sector 0 is valid.</summary>
    public bool Contains(int cylinder, int head, int sector) =>
        (uint)cylinder < (uint)Cylinders
        && (uint)head < (uint)Heads
        && (uint)sector < (uint)SectorsPerTrack;

    /// <summary>Linear index. Cylinder, then head, then sector.</summary>
    internal int Index(int cylinder, int head, int sector) =>
        (cylinder * Heads + head) * SectorsPerTrack + sector;
}

/// <summary>Why a sector access did not change or fill a buffer.</summary>
internal enum SectorResult
{
    /// <summary>The 256-byte sector was copied.</summary>
    Ok,

    /// <summary>The address is outside the geometry.</summary>
    NotFound,

    /// <summary>The image refuses writes. The bytes were left as they were.</summary>
    WriteProtected
}

/// <summary>
/// Geometry plus 256-byte sectors. <see cref="Parse"/> is the only reader of an HDV header.
/// The object stores geometry, version, container, and the protect bit. It does not keep header bytes.
/// Sector 0 is cylinder 0, head 0, sector 0.
/// </summary>
public sealed class HardDiskImage
{
    private const int HeaderLength = 256;
    private const byte Magic0 = 0x56;
    private const byte Magic1 = 0xCB;
    private const byte Version10 = 0x10;
    private const byte Version11 = 0x11;
    private const byte ProtectBit = 0x80;
    private const int VersionOffset = 2;
    private const int ProtectOffset = 7;
    private const int HeadsOffset = 26;
    private const int CylinderHighOffset = 27;
    private const int CylinderLowOffset = 28;
    private const int SectorsPerCylinderOffset = 29;
    private const int ClassicSectorsPerTrack = 32;

    private readonly byte[] _sectors;
    private readonly byte _version;
    private readonly Container _container;

    private HardDiskImage(
        HardDiskGeometry geometry,
        byte[] sectors,
        bool writeProtected,
        byte version,
        Container container)
    {
        Geometry = geometry;
        _sectors = sectors;
        WriteProtected = writeProtected;
        _version = version;
        _container = container;
    }

    private enum Container
    {
        Raw,
        Hdv
    }

    /// <summary>Address space of the sectors.</summary>
    public HardDiskGeometry Geometry { get; }

    /// <summary>Header bit 7 of byte 7, or the host flag. Set at parse and never cleared.</summary>
    public bool WriteProtected { get; }

    /// <summary>True after a successful sector write, until <see cref="MarkSaved"/>.</summary>
    public bool Modified { get; private set; }

    /// <summary>
    /// Reads an HDV file, or a raw body when <paramref name="rawGeometry"/> is set.
    /// Magic 56h CBh with a geometry argument is rejected. Raw bytes with no geometry are rejected.
    /// A short body, trailing bytes, a bad version, or a geometry that does not divide is rejected.
    /// </summary>
    public static HardDiskImage Parse(
        ReadOnlySpan<byte> file,
        HardDiskGeometry? rawGeometry = null,
        bool hostWriteProtected = false)
    {
        bool magic = file.Length >= 2 && file[0] == Magic0 && file[1] == Magic1;
        if (magic)
        {
            if (rawGeometry is not null)
                throw new InvalidDataException("An HDV image does not take a caller geometry.");

            return ParseHdv(file, hostWriteProtected);
        }

        if (rawGeometry is not { } geometry)
            throw new InvalidDataException("A raw hard-disk body needs a geometry.");

        return ParseRaw(file, geometry, hostWriteProtected);
    }

    /// <summary>
    /// HDV export writes a FreHD header from the stored fields, then the sectors.
    /// Version is the one that was imported. Byte 29 is sectors per cylinder, and 256 is stored as 0.
    /// Other header bytes are 0 except magic and the protect bit. Raw export writes the body only.
    /// </summary>
    public byte[] Export()
    {
        if (_container == Container.Raw)
            return [.. _sectors];

        int sectorsPerCylinder = Geometry.Heads * Geometry.SectorsPerTrack;
        var file = new byte[HeaderLength + _sectors.Length];
        file[0] = Magic0;
        file[1] = Magic1;
        file[VersionOffset] = _version;
        if (WriteProtected)
            file[ProtectOffset] = ProtectBit;

        file[HeadsOffset] = (byte)Geometry.Heads;
        file[CylinderHighOffset] = (byte)((Geometry.Cylinders >> 8) & 0x03);
        file[CylinderLowOffset] = (byte)Geometry.Cylinders;
        file[SectorsPerCylinderOffset] = sectorsPerCylinder == HardDiskGeometry.MaxSectorsPerTrack
            ? (byte)0
            : (byte)sectorsPerCylinder;
        _sectors.CopyTo(file, HeaderLength);
        return file;
    }

    internal SectorResult ReadSector(int cylinder, int head, int sector, Span<byte> destination)
    {
        if (destination.Length < HardDiskGeometry.BytesPerSector)
            throw new ArgumentException("A sector is 256 bytes.", nameof(destination));

        if (!Geometry.Contains(cylinder, head, sector))
            return SectorResult.NotFound;

        SectorSpan(cylinder, head, sector).CopyTo(destination);
        return SectorResult.Ok;
    }

    /// <summary>Writes all 256 bytes or none. WriteProtected and a missing address leave the image unchanged.</summary>
    internal SectorResult WriteSector(int cylinder, int head, int sector, ReadOnlySpan<byte> data)
    {
        if (data.Length != HardDiskGeometry.BytesPerSector)
            throw new ArgumentException("A sector is 256 bytes.", nameof(data));

        if (WriteProtected)
            return SectorResult.WriteProtected;

        if (!Geometry.Contains(cylinder, head, sector))
            return SectorResult.NotFound;

        data.CopyTo(SectorSpan(cylinder, head, sector));
        Modified = true;
        return SectorResult.Ok;
    }

    internal void MarkSaved() => Modified = false;

    private Span<byte> SectorSpan(int cylinder, int head, int sector) =>
        _sectors.AsSpan(
            Geometry.Index(cylinder, head, sector) * HardDiskGeometry.BytesPerSector,
            HardDiskGeometry.BytesPerSector);

    private static HardDiskImage ParseHdv(ReadOnlySpan<byte> file, bool hostWriteProtected)
    {
        if (file.Length < HeaderLength)
            throw new InvalidDataException("Hard-disk image is truncated.");

        byte version = file[VersionOffset];
        if (version is not (Version10 or Version11))
            throw new InvalidDataException("Hard-disk header version is not 10h or 11h.");

        // Byte 26 equal to 0 is the classic xtrs layout. FreHD stores the head count there.
        HardDiskGeometry geometry = file[HeadsOffset] switch
        {
            0 => ClassicGeometry(file),
            >= 1 and <= HardDiskGeometry.MaxHeads => FrehdGeometry(file),
            _ => throw new InvalidDataException("Hard-disk header head count is not classic or FreHD.")
        };

        bool writeProtected = hostWriteProtected || (file[ProtectOffset] & ProtectBit) != 0;
        return new HardDiskImage(geometry, CopyBody(file, HeaderLength, geometry), writeProtected, version, Container.Hdv);
    }

    private static HardDiskImage ParseRaw(ReadOnlySpan<byte> file, HardDiskGeometry geometry, bool hostWriteProtected) =>
        new(geometry, CopyBody(file, offset: 0, geometry), hostWriteProtected, version: 0, Container.Raw);

    private static HardDiskGeometry ClassicGeometry(ReadOnlySpan<byte> file)
    {
        int sectorsPerCylinder = CountOr256(file[SectorsPerCylinderOffset]);
        if (sectorsPerCylinder % ClassicSectorsPerTrack != 0)
            throw new InvalidDataException("Hard-disk header geometry does not divide into tracks.");

        int cylinders = CountOr256(file[CylinderLowOffset]);
        int heads = sectorsPerCylinder / ClassicSectorsPerTrack;
        return new HardDiskGeometry(cylinders, heads, ClassicSectorsPerTrack);
    }

    private static HardDiskGeometry FrehdGeometry(ReadOnlySpan<byte> file)
    {
        int cylinders = ((file[CylinderHighOffset] & 0x03) << 8) | file[CylinderLowOffset];
        if (cylinders == 0)
            throw new InvalidDataException("Hard-disk header cylinder count is zero.");

        int heads = file[HeadsOffset];
        int sectorsPerCylinder = CountOr256(file[SectorsPerCylinderOffset]);
        if (sectorsPerCylinder % heads != 0)
            throw new InvalidDataException("Hard-disk header geometry does not divide into tracks.");

        return new HardDiskGeometry(cylinders, heads, sectorsPerCylinder / heads);
    }

    private static byte[] CopyBody(ReadOnlySpan<byte> file, int offset, HardDiskGeometry geometry)
    {
        int sectorBytes = geometry.SectorCount * HardDiskGeometry.BytesPerSector;
        int expected = offset + sectorBytes;
        if (file.Length < expected)
            throw new InvalidDataException("Hard-disk image is truncated.");

        if (file.Length > expected)
            throw new InvalidDataException("Hard-disk image has trailing bytes.");

        return file.Slice(offset, sectorBytes).ToArray();
    }

    /// <summary>A stored 0 in a size byte means 256.</summary>
    private static int CountOr256(byte value) => value == 0 ? 256 : value;
}
