namespace Heh8080.Devices;

/// <summary>Host-independent parser and serializer for common TRS-80/CP-500 disk containers.</summary>
public static class ModelIIIDiskCodec
{
    private const int Jv3HeaderBytes = 2901;
    private const byte Jv3Empty = 0xFF;
    private const int DmkHeaderBytes = 16;

    /// <summary>Parses a disk image in the declared container format.</summary>
    public static ModelIIIDiskImage Parse(ReadOnlySpan<byte> bytes, ModelIIIDiskImageKind kind) => kind switch
    {
        ModelIIIDiskImageKind.Raw => ParseRaw(bytes),
        ModelIIIDiskImageKind.Jv1 => ParseJv1(bytes),
        ModelIIIDiskImageKind.Jv3 => ParseJv3(bytes),
        ModelIIIDiskImageKind.Dmk => ParseDmk(bytes),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Serializes a native image to the requested container format.</summary>
    public static byte[] Serialize(ModelIIIDiskImage image, ModelIIIDiskImageKind kind)
    {
        ArgumentNullException.ThrowIfNull(image);
        return kind switch
        {
            ModelIIIDiskImageKind.Raw => image.ToRawBytes(),
            ModelIIIDiskImageKind.Jv1 => image.ToRawBytes(),
            ModelIIIDiskImageKind.Jv3 => SerializeJv3(image),
            ModelIIIDiskImageKind.Dmk => SerializeDmk(image),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static ModelIIIDiskImage ParseRaw(ReadOnlySpan<byte> bytes)
    {
        var format = bytes.Length switch
        {
            ModelIIIDiskGeometry.DiskSizeBytes => ModelIIIDiskFormat.SingleSided,
            ModelIIIDiskGeometry.DoubleSidedDiskSizeBytes => ModelIIIDiskFormat.DoubleSided,
            _ => throw new InvalidDataException("Raw Model III images must be exactly 180 KiB or 360 KiB.")
        };

        return ModelIIIDiskImage.FromRaw(bytes, format);
    }

    private static ModelIIIDiskImage ParseJv1(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ModelIIIDiskGeometry.DiskSizeBytes)
            throw new InvalidDataException("JV1 Model III images must be exactly 180 KiB.");

        return new ModelIIIDiskImage(
            ModelIIIDiskFormat.SingleSided,
            ModelIIIDiskImage.FromRaw(bytes, ModelIIIDiskFormat.SingleSided).Sectors,
            ModelIIIDiskImageKind.Jv1);
    }

    private static ModelIIIDiskImage ParseJv3(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Jv3HeaderBytes)
            throw new InvalidDataException("JV3 image is shorter than its sector header.");

        var sectors = new List<ModelIIIDiskSector>();
        int dataOffset = Jv3HeaderBytes;
        int maxSide = 0;

        for (int entry = 0; entry < Jv3HeaderBytes / 3; entry++)
        {
            int headerOffset = entry * 3;
            byte track = bytes[headerOffset];
            byte id = bytes[headerOffset + 1];
            byte flags = bytes[headerOffset + 2];
            if (track == Jv3Empty && id == Jv3Empty && flags == Jv3Empty)
                continue;

            int side = (flags & 0x10) != 0 ? 1 : 0;
            int sizeCode = flags & 0x03;
            int length = 128 << sizeCode;
            if (dataOffset + length > bytes.Length)
                throw new InvalidDataException("JV3 image ends before a declared sector payload.");

            maxSide = Math.Max(maxSide, side);
            sectors.Add(new ModelIIIDiskSector(
                track,
                side,
                id,
                bytes.Slice(dataOffset, length).ToArray(),
                (flags & 0x80) != 0 ? ModelIIIDiskDensity.DoubleDensity : ModelIIIDiskDensity.SingleDensity,
                (flags & 0x04) != 0,
                (flags & 0x08) == 0));
            dataOffset += length;
        }

        if (sectors.Count == 0)
            throw new InvalidDataException("JV3 image contains no sectors.");

        return new ModelIIIDiskImage(maxSide == 0 ? ModelIIIDiskFormat.SingleSided : ModelIIIDiskFormat.DoubleSided, sectors, ModelIIIDiskImageKind.Jv3);
    }

    private static byte[] SerializeJv3(ModelIIIDiskImage image)
    {
        if (image.Sectors.Count > Jv3HeaderBytes / 3)
            throw new InvalidDataException("JV3 sector table cannot hold this many sectors.");

        using var stream = new MemoryStream();
        byte[] header = new byte[Jv3HeaderBytes];
        Array.Fill(header, Jv3Empty);
        int entry = 0;
        foreach (var sector in image.Sectors.OrderBy(sector => sector.Track).ThenBy(sector => sector.Side).ThenBy(sector => sector.Id))
        {
            int offset = entry++ * 3;
            byte flags = (byte)(sector.SizeCode & 0x03);
            if (sector.DeletedData) flags |= 0x04;
            if (!sector.CrcValid) flags |= 0x08;
            if (sector.Side != 0) flags |= 0x10;
            if (sector.Density == ModelIIIDiskDensity.DoubleDensity) flags |= 0x80;

            header[offset] = (byte)sector.Track;
            header[offset + 1] = (byte)sector.Id;
            header[offset + 2] = flags;
        }

        stream.Write(header);
        foreach (var sector in image.Sectors.OrderBy(sector => sector.Track).ThenBy(sector => sector.Side).ThenBy(sector => sector.Id))
            stream.Write(sector.Data);

        return stream.ToArray();
    }

    private static ModelIIIDiskImage ParseDmk(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < DmkHeaderBytes)
            throw new InvalidDataException("DMK image is shorter than its header.");

        int tracks = bytes[1];
        int trackLength = bytes[2] | (bytes[3] << 8);
        if (tracks <= 0 || tracks > ModelIIIDiskGeometry.Tracks || trackLength <= 0)
            throw new InvalidDataException("DMK image has invalid geometry.");
        if (DmkHeaderBytes + tracks * trackLength > bytes.Length)
            throw new InvalidDataException("DMK image ends before the declared tracks.");

        bool doubleSided = (bytes[4] & 0x10) == 0x00 && bytes.Length >= DmkHeaderBytes + tracks * trackLength * 2;
        int sides = doubleSided ? 2 : 1;
        var sectors = new List<ModelIIIDiskSector>();
        for (int track = 0; track < tracks; track++)
        for (int side = 0; side < sides; side++)
        {
            int trackOffset = DmkHeaderBytes + (track * sides + side) * trackLength;
            ParseDmkTrack(bytes.Slice(trackOffset, trackLength), track, side, sectors);
        }

        if (sectors.Count == 0)
            throw new InvalidDataException("DMK image contains no recognizable sectors.");

        return new ModelIIIDiskImage(sides == 1 ? ModelIIIDiskFormat.SingleSided : ModelIIIDiskFormat.DoubleSided, sectors, ModelIIIDiskImageKind.Dmk);
    }

    private static void ParseDmkTrack(ReadOnlySpan<byte> trackBytes, int expectedTrack, int expectedSide, List<ModelIIIDiskSector> sectors)
    {
        for (int offset = 0; offset + 10 < trackBytes.Length; offset++)
        {
            if (trackBytes[offset] != 0xFE)
                continue;

            int track = trackBytes[offset + 1];
            int side = trackBytes[offset + 2];
            int id = trackBytes[offset + 3];
            int sizeCode = trackBytes[offset + 4] & 0x03;
            int length = 128 << sizeCode;
            int dataMark = FindDmkDataMark(trackBytes, offset + 7);
            if (dataMark < 0 || dataMark + 1 + length > trackBytes.Length)
                continue;

            bool deleted = trackBytes[dataMark] == 0xF8;
            if (track != expectedTrack || side != expectedSide)
                continue;

            sectors.Add(new ModelIIIDiskSector(
                track,
                side,
                id,
                trackBytes.Slice(dataMark + 1, length).ToArray(),
                ModelIIIDiskDensity.SingleDensity,
                deleted,
                true));
            offset = dataMark + length;
        }
    }

    private static int FindDmkDataMark(ReadOnlySpan<byte> trackBytes, int start)
    {
        for (int i = start; i < trackBytes.Length; i++)
        {
            if (trackBytes[i] is 0xFB or 0xF8)
                return i;
        }

        return -1;
    }

    private static byte[] SerializeDmk(ModelIIIDiskImage image)
    {
        const int trackLength = 0x1900;
        int sides = ModelIIIDiskGeometry.GetSideCount(image.Format);
        using var stream = new MemoryStream();
        byte[] header = new byte[DmkHeaderBytes];
        header[1] = ModelIIIDiskGeometry.Tracks;
        header[2] = unchecked((byte)trackLength);
        header[3] = (byte)(trackLength >> 8);
        header[4] = sides == 1 ? (byte)0x10 : (byte)0x00;
        stream.Write(header);

        for (int track = 0; track < ModelIIIDiskGeometry.Tracks; track++)
        for (int side = 0; side < sides; side++)
        {
            byte[] trackBytes = new byte[trackLength];
            Array.Fill(trackBytes, (byte)0x4E);
            int offset = 128;
            foreach (var sector in image.Sectors.Where(sector => sector.Track == track && sector.Side == side).OrderBy(sector => sector.Id))
            {
                if (offset + sector.Data.Length + 16 >= trackBytes.Length)
                    break;

                trackBytes[offset++] = 0xFE;
                trackBytes[offset++] = (byte)sector.Track;
                trackBytes[offset++] = (byte)sector.Side;
                trackBytes[offset++] = (byte)sector.Id;
                trackBytes[offset++] = sector.SizeCode;
                trackBytes[offset++] = 0;
                trackBytes[offset++] = 0;
                trackBytes[offset++] = sector.DeletedData ? (byte)0xF8 : (byte)0xFB;
                sector.Data.CopyTo(trackBytes.AsSpan(offset));
                offset += sector.Data.Length;
                trackBytes[offset++] = 0;
                trackBytes[offset++] = 0;
            }

            stream.Write(trackBytes);
        }

        return stream.ToArray();
    }
}
