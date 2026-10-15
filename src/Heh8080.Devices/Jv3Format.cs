namespace Heh8080.Devices;

/// <summary>
/// JV3 container (Tim Mann / xtrs): blocks of 2901 three-byte sector headers plus a write-protect byte,
/// followed by that block's sector payloads. A further block may follow the payloads.
/// Sectors on tracks the Model III geometry cannot address are ignored on parse.
/// </summary>
internal static class Jv3Format
{
    private const int EntriesPerBlock = 2901;
    private const int HeaderBytes = EntriesPerBlock * 3;
    private const int BlockPrefixBytes = HeaderBytes + 1;
    private const byte Free = 0xFF;
    private const byte WriteProtected = 0xFF;

    private const byte DoubleDensityFlag = 0x80;
    private const byte DamMask = 0x60;
    private const byte DeletedSingleDensityDam = 0x60;
    private const byte DeletedDoubleDensityDam = 0x20;
    private const byte SideFlag = 0x10;
    private const byte CrcErrorFlag = 0x08;
    private const byte SizeMask = 0x03;

    private static readonly int[] UsedSizes = [256, 128, 1024, 512];
    private static readonly int[] FreeSizes = [512, 1024, 128, 256];

    public static ModelIIIDiskImage Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < BlockPrefixBytes)
            throw new InvalidDataException("JV3 image is shorter than its sector header.");

        var sectors = new List<ModelIIIDiskSector>();
        bool writeProtected = bytes[HeaderBytes] != 0;
        int blockStart = 0;
        while (blockStart + BlockPrefixBytes <= bytes.Length)
            blockStart = ParseBlock(bytes, blockStart, sectors);

        if (sectors.Count == 0)
            throw new InvalidDataException("JV3 image contains no sectors.");

        bool doubleSided = sectors.Any(sector => sector.Side != 0);
        return new ModelIIIDiskImage(
            doubleSided ? ModelIIIDiskFormat.DoubleSided : ModelIIIDiskFormat.SingleSided,
            sectors,
            ModelIIIDiskImageKind.Jv3,
            writeProtected);
    }

    public static byte[] Serialize(ModelIIIDiskImage image)
    {
        var sectors = image.Sectors;
        if (sectors.Count > EntriesPerBlock)
            throw new InvalidDataException("JV3 sector table cannot hold this many sectors.");

        byte[] prefix = new byte[BlockPrefixBytes];
        Array.Fill(prefix, Free, 0, HeaderBytes);
        prefix[HeaderBytes] = image.WriteProtected ? WriteProtected : (byte)0;

        using var stream = new MemoryStream();
        for (int entry = 0; entry < sectors.Count; entry++)
        {
            var sector = sectors[entry];
            int offset = entry * 3;
            prefix[offset] = (byte)sector.Track;
            prefix[offset + 1] = (byte)sector.Id;
            prefix[offset + 2] = ToFlags(sector);
        }

        stream.Write(prefix);
        foreach (var sector in sectors)
            stream.Write(sector.Data);

        return stream.ToArray();
    }

    private static int ParseBlock(ReadOnlySpan<byte> bytes, int blockStart, List<ModelIIIDiskSector> sectors)
    {
        int dataOffset = blockStart + BlockPrefixBytes;
        for (int entry = 0; entry < EntriesPerBlock; entry++)
        {
            int headerOffset = blockStart + entry * 3;
            byte track = bytes[headerOffset];
            byte id = bytes[headerOffset + 1];
            byte flags = bytes[headerOffset + 2];
            bool free = track == Free && id == Free;
            int length = (free ? FreeSizes : UsedSizes)[flags & SizeMask];
            bool payloadPresent = dataOffset + length <= bytes.Length;

            if (!free && !payloadPresent)
                throw new InvalidDataException("JV3 image ends before a declared sector payload.");
            if (free && !payloadPresent)
                continue;

            if (!free && track < ModelIIIDiskGeometry.Tracks)
                sectors.Add(ToSector(track, id, flags, bytes.Slice(dataOffset, length)));

            dataOffset += length;
        }

        return dataOffset;
    }

    private static ModelIIIDiskSector ToSector(byte track, byte id, byte flags, ReadOnlySpan<byte> data) =>
        new(
            track,
            (flags & SideFlag) != 0 ? 1 : 0,
            id,
            data.ToArray(),
            (flags & DoubleDensityFlag) != 0 ? ModelIIIDiskDensity.DoubleDensity : ModelIIIDiskDensity.SingleDensity,
            (flags & DamMask) != 0,
            (flags & CrcErrorFlag) == 0);

    private static byte ToFlags(ModelIIIDiskSector sector)
    {
        bool doubleDensity = sector.Density == ModelIIIDiskDensity.DoubleDensity;
        byte flags = (byte)Array.IndexOf(UsedSizes, sector.Data.Length);
        if (doubleDensity) flags |= DoubleDensityFlag;
        if (sector.DeletedData) flags |= doubleDensity ? DeletedDoubleDensityDam : DeletedSingleDensityDam;
        if (!sector.CrcValid) flags |= CrcErrorFlag;
        if (sector.Side != 0) flags |= SideFlag;
        return flags;
    }
}
