namespace Heh8080.Devices;

/// <summary>
/// DMK container (David Keil): a 16-byte header, then per track a 128-byte table of 64 little-endian
/// pointers to ID address marks (bit 15 = double density) followed by the raw FM/MFM track bytes.
/// Tracks the Model III geometry cannot address are ignored on parse.
/// </summary>
internal static class DmkFormat
{
    private const int HeaderBytes = 16;
    private const int IdamTableBytes = 128;
    private const int MaxIdamEntries = IdamTableBytes / 2;
    private const int MaxFileTracks = 80;
    private const int MaxGapToDataMark = 64;
    private const int IdFieldBytes = 7;
    private const int SerializedTrackLength = 0x1900;

    private const byte WriteProtected = 0xFF;
    private const byte SingleSidedFlag = 0x10;
    private const byte SingleDensityOnlyFlag = 0x40;
    private const byte IgnoreDensityFlag = 0x80;
    private const ushort DoubleDensityPointerFlag = 0x8000;
    private const ushort PointerOffsetMask = 0x3FFF;

    public static bool LooksLikeDmk(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes || bytes[0] is not (0 or WriteProtected))
            return false;

        int tracks = bytes[1];
        int trackLength = bytes[2] | (bytes[3] << 8);
        int sides = (bytes[4] & SingleSidedFlag) != 0 ? 1 : 2;
        return tracks is > 0 and <= MaxFileTracks && trackLength > IdamTableBytes &&
               HeaderBytes + (long)tracks * sides * trackLength == bytes.Length;
    }

    public static ModelIIIDiskImage Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes)
            throw new InvalidDataException("DMK image is shorter than its header.");

        int tracks = bytes[1];
        int trackLength = bytes[2] | (bytes[3] << 8);
        byte flags = bytes[4];
        if (tracks is <= 0 or > MaxFileTracks || trackLength <= IdamTableBytes)
            throw new InvalidDataException("DMK image has invalid geometry.");

        int sides = (flags & SingleSidedFlag) != 0 ? 1 : 2;
        if (HeaderBytes + (long)tracks * sides * trackLength > bytes.Length)
            throw new InvalidDataException("DMK image ends before the declared tracks.");

        var sectors = new List<ModelIIIDiskSector>();
        for (int track = 0; track < Math.Min(tracks, ModelIIIDiskGeometry.Tracks); track++)
        for (int side = 0; side < sides; side++)
        {
            int trackOffset = HeaderBytes + (track * sides + side) * trackLength;
            ParseTrack(bytes.Slice(trackOffset, trackLength), flags, track, side, sectors);
        }

        if (sectors.Count == 0)
            throw new InvalidDataException("DMK image contains no recognizable sectors.");

        return new ModelIIIDiskImage(
            sides == 1 ? ModelIIIDiskFormat.SingleSided : ModelIIIDiskFormat.DoubleSided,
            sectors,
            ModelIIIDiskImageKind.Dmk,
            bytes[0] == WriteProtected);
    }

    public static byte[] Serialize(ModelIIIDiskImage image)
    {
        int sides = ModelIIIDiskGeometry.GetSideCount(image.Format);
        var sectors = image.Sectors;
        bool singleDensityOnly = sectors.Count > 0 && sectors.All(sector => sector.Density == ModelIIIDiskDensity.SingleDensity);

        byte[] file = new byte[HeaderBytes + ModelIIIDiskGeometry.Tracks * sides * SerializedTrackLength];
        file[0] = image.WriteProtected ? WriteProtected : (byte)0;
        file[1] = ModelIIIDiskGeometry.Tracks;
        file[2] = unchecked((byte)SerializedTrackLength);
        file[3] = (byte)(SerializedTrackLength >> 8);
        file[4] = (byte)((sides == 1 ? SingleSidedFlag : 0) | (singleDensityOnly ? SingleDensityOnlyFlag : 0));

        for (int track = 0; track < ModelIIIDiskGeometry.Tracks; track++)
        for (int side = 0; side < sides; side++)
        {
            int trackOffset = HeaderBytes + (track * sides + side) * SerializedTrackLength;
            SerializeTrack(image.GetTrack(track, side), file.AsSpan(trackOffset, SerializedTrackLength), singleDensityOnly);
        }
        
        return file;
    }

    private static void SerializeTrack(IReadOnlyList<ModelIIIDiskSector> sectors, Span<byte> track, bool singleDensityOnly)
    {
        if (sectors.Count > MaxIdamEntries)
            throw new InvalidDataException("DMK track cannot index this many sectors.");

        var idams = new List<(int Offset, ModelIIIDiskDensity Density)>();
        bool fits = ModelIIITrackFormat.Encode(
            sectors,
            track[IdamTableBytes..],
            singleDensityOnly ? ModelIIIDiskDensity.SingleDensity : ModelIIIDiskDensity.DoubleDensity,
            doubleFmBytes: !singleDensityOnly,
            idams);
        if (!fits)
            throw new InvalidDataException("DMK track is too small for the sectors on this track.");

        for (int index = 0; index < idams.Count; index++)
        {
            int pointer = IdamTableBytes + idams[index].Offset;
            if (idams[index].Density == ModelIIIDiskDensity.DoubleDensity)
                pointer |= DoubleDensityPointerFlag;

            track[index * 2] = (byte)pointer;
            track[index * 2 + 1] = (byte)(pointer >> 8);
        }
    }

    private static void ParseTrack(
        ReadOnlySpan<byte> trackBytes,
        byte flags,
        int track,
        int side,
        List<ModelIIIDiskSector> sectors)
    {
        bool singleDensityOnly = (flags & SingleDensityOnlyFlag) != 0;
        bool bytesDoubled = (flags & (SingleDensityOnlyFlag | IgnoreDensityFlag)) == 0;

        for (int entry = 0; entry < MaxIdamEntries; entry++)
        {
            int pointer = trackBytes[entry * 2] | (trackBytes[entry * 2 + 1] << 8);
            if (pointer == 0)
                break;

            var density = !singleDensityOnly && (pointer & DoubleDensityPointerFlag) != 0
                ? ModelIIIDiskDensity.DoubleDensity
                : ModelIIIDiskDensity.SingleDensity;
            int stride = density == ModelIIIDiskDensity.SingleDensity && bytesDoubled ? 2 : 1;
            var sector = DecodeSector(trackBytes, pointer & PointerOffsetMask, stride, density, track, side);
            if (sector is not null)
                sectors.Add(sector);
        }
    }

    private static ModelIIIDiskSector? DecodeSector(
        ReadOnlySpan<byte> trackBytes,
        int offset,
        int stride,
        ModelIIIDiskDensity density,
        int track,
        int side)
    {
        if (offset < IdamTableBytes || ReadUnits(trackBytes, offset, stride, 0, IdFieldBytes) is not { } idField ||
            idField[0] != ModelIIITrackFormat.IdAddressMark || idField[4] > 3)
        {
            return null;
        }

        bool idCrcValid = ModelIIIDiskCrc.ForField(density, ModelIIITrackFormat.IdAddressMark, idField.AsSpan(1, 4)) ==
                          ReadCrc(idField, 5);
        int dataMarkUnit = FindDataMark(trackBytes, offset, stride);
        int length = 128 << idField[4];
        if (dataMarkUnit < 0 ||
            ReadUnits(trackBytes, offset, stride, dataMarkUnit, 1 + length + 2) is not { } dataField)
        {
            return null;
        }

        var data = dataField.AsSpan(1, length);
        bool dataCrcValid = ModelIIIDiskCrc.ForField(density, dataField[0], data) == ReadCrc(dataField, 1 + length);
        return new ModelIIIDiskSector(
            track,
            side,
            idField[3],
            data.ToArray(),
            density,
            dataField[0] != ModelIIITrackFormat.DataAddressMark,
            idCrcValid && dataCrcValid,
            idField[1]);
    }

    private static int FindDataMark(ReadOnlySpan<byte> trackBytes, int offset, int stride)
    {
        for (int unit = IdFieldBytes; unit < IdFieldBytes + MaxGapToDataMark; unit++)
        {
            int position = offset + unit * stride;
            if (position >= trackBytes.Length)
                break;

            if (trackBytes[position] is >= 0xF8 and <= 0xFB)
                return unit;
        }

        return -1;
    }

    private static byte[]? ReadUnits(ReadOnlySpan<byte> trackBytes, int offset, int stride, int firstUnit, int count)
    {
        if (offset + (firstUnit + count - 1) * stride >= trackBytes.Length)
            return null;

        byte[] units = new byte[count];
        for (int index = 0; index < count; index++)
            units[index] = trackBytes[offset + (firstUnit + index) * stride];

        return units;
    }

    private static int ReadCrc(byte[] field, int offset) => (field[offset] << 8) | field[offset + 1];
}
