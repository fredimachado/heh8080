using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class ModelIIIDiskCodecTests
{
    private const int Jv3EntryCount = 2901;
    private const int Jv3HeaderBytes = Jv3EntryCount * 3;
    private const int Jv3DataOffset = 34 * 256;
    private const int DmkHeaderBytes = 16;
    private const int DmkTableBytes = 128;
    private const int DmkTrackLength = 0x1900;

    [Fact]
    public void RawImagesRoundTripAndRejectOddSizes()
    {
        var image = ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.SingleSided);
        image.FindSector(0, 0, 1)!.Data[0] = 0xA5;

        var parsed = ModelIIIDiskCodec.Parse(ModelIIIDiskCodec.Serialize(image, ModelIIIDiskImageKind.Raw), ModelIIIDiskImageKind.Raw);

        Assert.Equal(0xA5, parsed.FindSector(0, 0, 1)!.Data[0]);
        Assert.Throws<InvalidDataException>(() => ModelIIIDiskCodec.Parse(new byte[123], ModelIIIDiskImageKind.Raw));
    }

    [Fact]
    public void RawAndBlankSectorsAreDoubleDensity()
    {
        var blank = ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.SingleSided);
        var raw = ModelIIIDiskImage.FromRaw(new byte[ModelIIIDiskGeometry.DiskSizeBytes], ModelIIIDiskFormat.SingleSided);

        Assert.All(blank.Sectors, sector => Assert.Equal(ModelIIIDiskDensity.DoubleDensity, sector.Density));
        Assert.All(raw.Sectors, sector => Assert.Equal(ModelIIIDiskDensity.DoubleDensity, sector.Density));
        Assert.True((ModelIIIDiskCodec.Serialize(blank, ModelIIIDiskImageKind.Jv3)[2] & 0x80) != 0);

        byte[] dmk = ModelIIIDiskCodec.Serialize(blank, ModelIIIDiskImageKind.Dmk);
        Assert.True((dmk[DmkHeaderBytes + 1] & 0x80) != 0);
    }

    [Fact]
    public void ImageIndexesSectorsByTrackAndSide()
    {
        var image = ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.DoubleSided);

        Assert.Equal(Enumerable.Range(1, 18), image.GetTrack(7, 1).Select(sector => sector.Id));
        Assert.All(image.GetTrack(7, 1), sector => Assert.Equal((7, 1), (sector.Track, sector.Side)));
        Assert.Empty(image.GetTrack(40, 0));
        Assert.Equal(3, image.FindSector(39, 1, 3)!.Id);
        Assert.Null(image.FindSector(39, 1, 19));
    }

    [Fact]
    public void Jv3SectorDataStartsAtOffset8704AndHonorsUsedSizeCodes()
    {
        byte[] jv3 = BuildJv3(
            writeProtect: false,
            Entry(0, 1, 0x80 | 0, Fill(256, 0x11)),
            Entry(0, 2, 0x80 | 1, Fill(128, 0x22)),
            Entry(0, 3, 0x80 | 2, Fill(1024, 0x33)),
            Entry(0, 4, 0x80 | 3, Fill(512, 0x44)));

        Assert.Equal(Jv3DataOffset, jv3.Length - (256 + 128 + 1024 + 512));
        var image = ModelIIIDiskCodec.Parse(jv3, ModelIIIDiskImageKind.Jv3);

        Assert.Equal(256, image.FindSector(0, 0, 1)!.Data.Length);
        Assert.All(image.FindSector(0, 0, 1)!.Data, value => Assert.Equal(0x11, value));
        Assert.Equal(128, image.FindSector(0, 0, 2)!.Data.Length);
        Assert.All(image.FindSector(0, 0, 2)!.Data, value => Assert.Equal(0x22, value));
        Assert.Equal(1024, image.FindSector(0, 0, 3)!.Data.Length);
        Assert.All(image.FindSector(0, 0, 3)!.Data, value => Assert.Equal(0x33, value));
        Assert.Equal(512, image.FindSector(0, 0, 4)!.Data.Length);
        Assert.All(image.FindSector(0, 0, 4)!.Data, value => Assert.Equal(0x44, value));
    }

    [Fact]
    public void Jv3FlagsDecodeDensityDeletedMarkCrcErrorAndSide()
    {
        byte[] jv3 = BuildJv3(
            writeProtect: false,
            Entry(0, 1, 0x80, Fill(256, 1)),
            Entry(0, 2, 0x80 | 0x20, Fill(256, 2)),
            Entry(0, 3, 0x60, Fill(256, 3)),
            Entry(0, 4, 0x00, Fill(256, 4)),
            Entry(1, 5, 0x10 | 0x08 | 0x80, Fill(256, 5)));

        var image = ModelIIIDiskCodec.Parse(jv3, ModelIIIDiskImageKind.Jv3);

        Assert.Equal(ModelIIIDiskFormat.DoubleSided, image.Format);
        var normal = image.FindSector(0, 0, 1)!;
        Assert.Equal(ModelIIIDiskDensity.DoubleDensity, normal.Density);
        Assert.False(normal.DeletedData);
        Assert.True(normal.CrcValid);
        Assert.True(image.FindSector(0, 0, 2)!.DeletedData);
        Assert.Equal(ModelIIIDiskDensity.SingleDensity, image.FindSector(0, 0, 3)!.Density);
        Assert.True(image.FindSector(0, 0, 3)!.DeletedData);
        Assert.Equal(ModelIIIDiskDensity.SingleDensity, image.FindSector(0, 0, 4)!.Density);
        Assert.False(image.FindSector(0, 0, 4)!.DeletedData);
        var errored = image.FindSector(1, 1, 5)!;
        Assert.Equal(1, errored.Side);
        Assert.False(errored.CrcValid);
        Assert.Equal(ModelIIIDiskDensity.DoubleDensity, errored.Density);
    }

    [Fact]
    public void Jv3FreeEntriesOccupyDataSpaceWithTheirOwnSizeCodes()
    {
        byte[] jv3 = BuildJv3(
            writeProtect: false,
            Entry(0, 1, 0x80, Fill(256, 1)),
            Entry(0xFF, 0xFF, 0xFC | 0, Fill(512, 0xEE)),
            Entry(0xFF, 0xFF, 0xFC | 1, Fill(1024, 0xEE)),
            Entry(0xFF, 0xFF, 0xFC | 2, Fill(128, 0xEE)),
            Entry(0xFF, 0xFF, 0xFC | 3, Fill(256, 0xEE)),
            Entry(0, 2, 0x80, Fill(256, 2)));

        var image = ModelIIIDiskCodec.Parse(jv3, ModelIIIDiskImageKind.Jv3);

        Assert.Equal(2, image.Sectors.Count);
        Assert.All(image.FindSector(0, 0, 2)!.Data, value => Assert.Equal(2, value));
    }

    [Fact]
    public void Jv3ReadsASecondHeaderBlockAfterTheFirstBlocksData()
    {
        var firstEntries = new[] { Entry(0, 1, 0x80, Fill(256, 1)) }
            .Concat(Enumerable.Repeat(Entry(0xFF, 0xFF, 0xFC | 2, Fill(128, 0xEE)), Jv3EntryCount - 1))
            .ToArray();
        byte[] first = BuildJv3(false, firstEntries);
        byte[] second = BuildJv3(false, Entry(2, 1, 0x80, Fill(256, 9)));

        var image = ModelIIIDiskCodec.Parse([.. first, .. second], ModelIIIDiskImageKind.Jv3);

        Assert.Equal(2, image.Sectors.Count);
        Assert.All(image.FindSector(2, 0, 1)!.Data, value => Assert.Equal(9, value));
    }

    [Fact]
    public void Jv3WriteProtectByteMakesTheMountReadOnly()
    {
        byte[] protectedDisk = BuildJv3(writeProtect: true, Entry(0, 1, 0x80, Fill(256, 1)));
        byte[] openDisk = BuildJv3(writeProtect: false, Entry(0, 1, 0x80, Fill(256, 1)));
        var images = new MemoryModelIIIDiskImageProvider();

        images.Mount(0, protectedDisk, ModelIIIDiskImageKind.Jv3);
        images.Mount(1, openDisk, ModelIIIDiskImageKind.Jv3);

        Assert.True(images.IsReadOnly(0));
        Assert.False(images.IsReadOnly(1));
        Assert.True(ModelIIIDiskCodec.Parse(protectedDisk, ModelIIIDiskImageKind.Jv3).WriteProtected);
        Assert.Equal(0xFF, ModelIIIDiskCodec.Serialize(ModelIIIDiskCodec.Parse(protectedDisk, ModelIIIDiskImageKind.Jv3), ModelIIIDiskImageKind.Jv3)[Jv3HeaderBytes]);
        Assert.Equal(0x00, ModelIIIDiskCodec.Serialize(ModelIIIDiskCodec.Parse(openDisk, ModelIIIDiskImageKind.Jv3), ModelIIIDiskImageKind.Jv3)[Jv3HeaderBytes]);
    }

    [Fact]
    public void Jv3RejectsTruncatedPayloadsAndShortFiles()
    {
        byte[] jv3 = BuildJv3(false, Entry(0, 1, 0x80, Fill(256, 1)));

        Assert.Throws<InvalidDataException>(() => ModelIIIDiskCodec.Parse(jv3[..^1], ModelIIIDiskImageKind.Jv3));
        Assert.Throws<InvalidDataException>(() => ModelIIIDiskCodec.Parse(new byte[100], ModelIIIDiskImageKind.Jv3));
    }

    [Fact]
    public void Jv3SerializationMatchesTheSpecLayout()
    {
        var sectors = new[]
        {
            new ModelIIIDiskSector(0, 0, 1, Fill(256, 0xA1)),
            new ModelIIIDiskSector(0, 0, 2, Fill(128, 0xA2), DeletedData: true),
            new ModelIIIDiskSector(0, 0, 3, Fill(1024, 0xA3), CrcValid: false),
            new ModelIIIDiskSector(0, 1, 4, Fill(512, 0xA4)),
            new ModelIIIDiskSector(1, 0, 5, Fill(256, 0xA5), ModelIIIDiskDensity.SingleDensity, DeletedData: true)
        };
        var image = new ModelIIIDiskImage(ModelIIIDiskFormat.DoubleSided, sectors);

        byte[] jv3 = ModelIIIDiskCodec.Serialize(image, ModelIIIDiskImageKind.Jv3);

        Assert.Equal(Jv3DataOffset + 256 + 128 + 1024 + 512 + 256, jv3.Length);
        Assert.Equal(new byte[] { 0, 1, 0x80 }, jv3[0..3]);
        Assert.Equal(new byte[] { 0, 2, 0x80 | 0x20 | 1 }, jv3[3..6]);
        Assert.Equal(new byte[] { 0, 3, 0x80 | 0x08 | 2 }, jv3[6..9]);
        Assert.Equal(new byte[] { 0, 4, 0x80 | 0x10 | 3 }, jv3[9..12]);
        Assert.Equal(new byte[] { 1, 5, 0x60 }, jv3[12..15]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, jv3[15..18]);
        Assert.Equal(0, jv3[Jv3HeaderBytes]);
        Assert.Equal(0xA1, jv3[Jv3DataOffset]);
        Assert.Equal(0xA2, jv3[Jv3DataOffset + 256]);
        Assert.Equal(0xA3, jv3[Jv3DataOffset + 256 + 128]);
    }

    [Fact]
    public void DmkParsingFollowsTheIdamPointerTableAndIgnoresStrayMarkBytes()
    {
        var builder = new DmkTrackBuilder(mfm: true, doubled: false);
        byte[] decoy = Fill(256, 0x00);
        byte[] fakeId = [0xFE, 0x00, 0x00, 0x09, 0x01, 0x12, 0x34, 0xFB];
        fakeId.CopyTo(decoy, 10);
        builder.AddSector(0, 0, 1, 1, decoy);
        builder.AddSector(0, 0, 2, 1, Fill(256, 0x55));

        var image = ModelIIIDiskCodec.Parse(BuildDmk(flags: 0x10, tracks: 1, builder.Build()), ModelIIIDiskImageKind.Dmk);

        Assert.Equal(new[] { 1, 2 }, image.Sectors.Select(sector => sector.Id));
        Assert.Equal(decoy, image.FindSector(0, 0, 1)!.Data);
        Assert.All(image.Sectors, sector => Assert.Equal(ModelIIIDiskDensity.DoubleDensity, sector.Density));
        Assert.All(image.Sectors, sector => Assert.True(sector.CrcValid));
    }

    [Fact]
    public void DmkVerifiesIdAndDataCrcsAndMarksDeletedData()
    {
        var builder = new DmkTrackBuilder(mfm: true, doubled: false);
        builder.AddSector(0, 0, 1, 1, Fill(256, 1));
        builder.AddSector(0, 0, 2, 1, Fill(256, 2), deleted: true);
        builder.AddSector(0, 0, 3, 1, Fill(256, 3), badDataCrc: true);
        builder.AddSector(0, 0, 4, 1, Fill(256, 4), badIdCrc: true);

        var image = ModelIIIDiskCodec.Parse(BuildDmk(0x10, 1, builder.Build()), ModelIIIDiskImageKind.Dmk);

        Assert.True(image.FindSector(0, 0, 1)!.CrcValid);
        Assert.False(image.FindSector(0, 0, 1)!.DeletedData);
        Assert.True(image.FindSector(0, 0, 2)!.DeletedData);
        Assert.False(image.FindSector(0, 0, 3)!.CrcValid);
        Assert.False(image.FindSector(0, 0, 4)!.CrcValid);
    }

    [Fact]
    public void DmkSingleDensityBytesAreDoubledUnlessTheHeaderSaysOtherwise()
    {
        var doubled = new DmkTrackBuilder(mfm: false, doubled: true);
        doubled.AddSector(0, 0, 1, 0, Fill(128, 0x5A));
        var plain = new DmkTrackBuilder(mfm: false, doubled: false);
        plain.AddSector(0, 0, 1, 0, Fill(128, 0x5A));

        var fromDoubled = ModelIIIDiskCodec.Parse(BuildDmk(0x10, 1, doubled.Build()), ModelIIIDiskImageKind.Dmk);
        var fromSingleOnly = ModelIIIDiskCodec.Parse(BuildDmk(0x10 | 0x40, 1, plain.Build()), ModelIIIDiskImageKind.Dmk);
        var fromIgnoreDensity = ModelIIIDiskCodec.Parse(BuildDmk(0x10 | 0x80, 1, plain.Build()), ModelIIIDiskImageKind.Dmk);

        foreach (var image in new[] { fromDoubled, fromSingleOnly, fromIgnoreDensity })
        {
            var sector = image.FindSector(0, 0, 1)!;
            Assert.Equal(ModelIIIDiskDensity.SingleDensity, sector.Density);
            Assert.Equal(Fill(128, 0x5A), sector.Data);
            Assert.True(sector.CrcValid);
        }
    }

    [Fact]
    public void DmkAcceptsEightyTracksButMapsOnlyTheModelIIIGeometry()
    {
        byte[] populated = BuildPopulatedTrack(0);
        byte[] beyond = BuildPopulatedTrack(50);
        byte[] empty = new byte[DmkTrackLength];
        var tracks = Enumerable.Range(0, 80).Select(track => track == 0 ? populated : track == 50 ? beyond : empty);

        var image = ModelIIIDiskCodec.Parse(BuildDmk(0x10, 80, [.. tracks.SelectMany(track => track)]), ModelIIIDiskImageKind.Dmk);

        Assert.Single(image.Sectors);
        Assert.Equal(0, image.Sectors[0].Track);
        Assert.Throws<InvalidDataException>(() =>
            ModelIIIDiskCodec.Parse(BuildDmk(0x10, 81, new byte[81 * DmkTrackLength]), ModelIIIDiskImageKind.Dmk));
    }

    [Fact]
    public void DmkReadsDoubleSidedTracksInterleavedBySide()
    {
        var side0 = new DmkTrackBuilder(mfm: true, doubled: false);
        side0.AddSector(0, 0, 1, 1, Fill(256, 0x10));
        var side1 = new DmkTrackBuilder(mfm: true, doubled: false);
        side1.AddSector(0, 1, 1, 1, Fill(256, 0x20));

        var image = ModelIIIDiskCodec.Parse(BuildDmk(0x00, 1, [.. side0.Build(), .. side1.Build()]), ModelIIIDiskImageKind.Dmk);

        Assert.Equal(ModelIIIDiskFormat.DoubleSided, image.Format);
        Assert.Equal(0x10, image.FindSector(0, 0, 1)!.Data[0]);
        Assert.Equal(0x20, image.FindSector(0, 1, 1)!.Data[0]);
    }

    [Fact]
    public void DmkWriteProtectByteIsHonoredAndWritten()
    {
        var builder = new DmkTrackBuilder(mfm: true, doubled: false);
        builder.AddSector(0, 0, 1, 1, Fill(256, 1));
        byte[] file = BuildDmk(0x10, 1, builder.Build());
        file[0] = 0xFF;

        var image = ModelIIIDiskCodec.Parse(file, ModelIIIDiskImageKind.Dmk);

        Assert.True(image.WriteProtected);
        Assert.Equal(0xFF, ModelIIIDiskCodec.Serialize(image, ModelIIIDiskImageKind.Dmk)[0]);
    }

    [Fact]
    public void DmkSerializationWritesIdamPointersMfmLayoutAndRealCrcs()
    {
        var image = ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.SingleSided);
        image.FindSector(0, 0, 2)!.Data[5] = 0x77;

        byte[] dmk = ModelIIIDiskCodec.Serialize(image, ModelIIIDiskImageKind.Dmk);

        Assert.Equal(40, dmk[1]);
        Assert.Equal(DmkTrackLength, dmk[2] | (dmk[3] << 8));
        Assert.Equal(0x10, dmk[4] & 0x90);
        var track = dmk.AsSpan(DmkHeaderBytes + 3 * DmkTrackLength, DmkTrackLength);
        for (int sector = 1; sector <= 18; sector++)
        {
            int pointer = track[(sector - 1) * 2] | (track[(sector - 1) * 2 + 1] << 8);
            Assert.True((pointer & 0x8000) != 0);
            Assert.Equal(0, pointer & 0x4000);
            int idam = pointer & 0x3FFF;
            Assert.True(idam >= DmkTableBytes);
            AssertMfmSectorAt(track, idam, 3, 0, sector, 256);
        }

        Assert.Equal(0, track[18 * 2] | (track[18 * 2 + 1] << 8));
    }

    [Fact]
    public void DmkSerializationDoublesFmBytesOnlyWhenDensitiesAreMixed()
    {
        var sectors = new[]
        {
            new ModelIIIDiskSector(0, 0, 1, Fill(128, 0x33), ModelIIIDiskDensity.SingleDensity),
            new ModelIIIDiskSector(0, 0, 2, Fill(256, 0x44))
        };
        var mixed = new ModelIIIDiskImage(ModelIIIDiskFormat.SingleSided, sectors);
        var singleOnly = new ModelIIIDiskImage(ModelIIIDiskFormat.SingleSided, sectors.Take(1));

        byte[] mixedFile = ModelIIIDiskCodec.Serialize(mixed, ModelIIIDiskImageKind.Dmk);
        byte[] singleFile = ModelIIIDiskCodec.Serialize(singleOnly, ModelIIIDiskImageKind.Dmk);

        Assert.Equal(0, mixedFile[4] & 0xC0);
        Assert.Equal(0x40, singleFile[4] & 0xC0);
        int mixedPointer = mixedFile[DmkHeaderBytes] | (mixedFile[DmkHeaderBytes + 1] << 8);
        Assert.Equal(0, mixedPointer & 0x8000);
        Assert.Equal(0xFE, mixedFile[DmkHeaderBytes + (mixedPointer & 0x3FFF)]);
        Assert.Equal(0xFE, mixedFile[DmkHeaderBytes + (mixedPointer & 0x3FFF) + 1]);
        int singlePointer = singleFile[DmkHeaderBytes] | (singleFile[DmkHeaderBytes + 1] << 8);
        Assert.NotEqual(0xFE, singleFile[DmkHeaderBytes + (singlePointer & 0x3FFF) + 1]);
    }

    [Theory]
    [InlineData(ModelIIIDiskImageKind.Jv3)]
    [InlineData(ModelIIIDiskImageKind.Dmk)]
    public void ContainersRoundTripSectorMetadata(ModelIIIDiskImageKind kind)
    {
        var sectors = new[]
        {
            new ModelIIIDiskSector(0, 0, 1, Fill(256, 0xA5)),
            new ModelIIIDiskSector(0, 0, 2, Fill(256, 0x01), DeletedData: true),
            new ModelIIIDiskSector(0, 0, 3, Fill(256, 0x02), CrcValid: false),
            new ModelIIIDiskSector(1, 1, 1, Fill(512, 0x03)),
            new ModelIIIDiskSector(1, 1, 5, Fill(128, 0x04)),
            new ModelIIIDiskSector(2, 0, 7, Fill(256, 0x05), ModelIIIDiskDensity.SingleDensity),
            new ModelIIIDiskSector(2, 0, 8, Fill(256, 0x06), ModelIIIDiskDensity.SingleDensity, DeletedData: true)
        };
        var image = new ModelIIIDiskImage(ModelIIIDiskFormat.DoubleSided, sectors);

        var parsed = ModelIIIDiskCodec.Parse(ModelIIIDiskCodec.Serialize(image, kind), kind);

        Assert.Equal(kind, parsed.SourceKind);
        Assert.Equal(Describe(image), Describe(parsed));
    }

    [Theory]
    [InlineData(ModelIIIDiskImageKind.Jv3)]
    [InlineData(ModelIIIDiskImageKind.Dmk)]
    public void ProviderExportsTheImageInItsSourceContainer(ModelIIIDiskImageKind kind)
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.Mount(0, ModelIIIDiskCodec.Serialize(ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.SingleSided), kind), kind);
        Assert.True(images.WriteSector(0, 4, 0, 6, Fill(256, 0x99)));

        var exported = ModelIIIDiskCodec.Parse(images.GetImageBytes(0), kind);

        Assert.Equal(0x99, exported.FindSector(4, 0, 6)!.Data[0]);
    }

    private static string[] Describe(ModelIIIDiskImage image) => image.Sectors
        .Select(sector => $"{sector.Track}/{sector.Side}/{sector.Id}/{sector.Density}/{sector.DeletedData}/{sector.CrcValid}/{Convert.ToHexString(sector.Data)}")
        .ToArray();

    private static void AssertMfmSectorAt(ReadOnlySpan<byte> track, int idam, int cylinder, int side, int sector, int length)
    {
        Assert.Equal(0xFE, track[idam]);
        Assert.Equal(new byte[] { (byte)cylinder, (byte)side, (byte)sector, 1 }, track.Slice(idam + 1, 4).ToArray());
        Assert.Equal(Crc(mfm: true, track.Slice(idam, 5).ToArray()), (track[idam + 5] << 8) | track[idam + 6]);

        int mark = idam + 7;
        while (track[mark] is < 0xF8 or > 0xFB)
            mark++;

        Assert.Equal(new byte[] { 0xA1, 0xA1, 0xA1 }, track.Slice(mark - 3, 3).ToArray());
        Assert.Equal(0xFB, track[mark]);
        int crcOffset = mark + 1 + length;
        Assert.Equal(Crc(mfm: true, track.Slice(mark, 1 + length).ToArray()), (track[crcOffset] << 8) | track[crcOffset + 1]);
    }

    private static byte[] BuildPopulatedTrack(int cylinder)
    {
        var builder = new DmkTrackBuilder(mfm: true, doubled: false);
        builder.AddSector(cylinder, 0, 1, 1, Fill(256, 1));
        return builder.Build();
    }

    private static byte[] BuildDmk(byte flags, int tracks, byte[] trackData)
    {
        byte[] file = new byte[DmkHeaderBytes + trackData.Length];
        file[1] = (byte)tracks;
        file[2] = unchecked((byte)DmkTrackLength);
        file[3] = DmkTrackLength >> 8;
        file[4] = flags;
        trackData.CopyTo(file, DmkHeaderBytes);
        return file;
    }

    private static byte[] BuildJv3(bool writeProtect, params (byte Track, byte Id, byte Flags, byte[] Data)[] entries)
    {
        byte[] header = new byte[Jv3HeaderBytes + 1];
        Array.Fill(header, (byte)0xFF, 0, Jv3HeaderBytes);
        header[Jv3HeaderBytes] = writeProtect ? (byte)0xFF : (byte)0x00;
        for (int index = 0; index < entries.Length; index++)
        {
            header[index * 3] = entries[index].Track;
            header[index * 3 + 1] = entries[index].Id;
            header[index * 3 + 2] = entries[index].Flags;
        }

        return [.. header, .. entries.SelectMany(entry => entry.Data)];
    }

    private static (byte Track, byte Id, byte Flags, byte[] Data) Entry(int track, int id, int flags, byte[] data) =>
        ((byte)track, (byte)id, (byte)flags, data);

    private static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private static int Crc(bool mfm, byte[] bytes)
    {
        int crc = 0xFFFF;
        foreach (byte value in mfm ? [0xA1, 0xA1, 0xA1, .. bytes] : bytes)
        {
            crc ^= value << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }

        return crc;
    }

    private sealed class DmkTrackBuilder(bool mfm, bool doubled)
    {
        private readonly byte[] _bytes = new byte[DmkTrackLength];
        private readonly List<int> _idams = [];
        private int _position = DmkTableBytes;

        public void AddSector(
            int cylinder,
            int side,
            int id,
            int sizeCode,
            byte[] data,
            bool deleted = false,
            bool badDataCrc = false,
            bool badIdCrc = false)
        {
            byte gap = mfm ? (byte)0x4E : (byte)0xFF;
            byte[] idField = [0xFE, (byte)cylinder, (byte)side, (byte)id, (byte)sizeCode];
            byte dataMark = deleted ? (byte)0xF8 : (byte)0xFB;
            byte[] dataField = [dataMark, .. data];
            int idCrc = Crc(mfm, idField) ^ (badIdCrc ? 0x0101 : 0);
            int dataCrc = Crc(mfm, dataField) ^ (badDataCrc ? 0x0101 : 0);

            Put(0x00, mfm ? 12 : 6);
            if (mfm)
                Put(0xA1, 3);

            _idams.Add(_position | (mfm ? 0x8000 : 0));
            foreach (byte value in idField)
                Put(value, 1);

            Put((byte)(idCrc >> 8), 1);
            Put((byte)idCrc, 1);
            Put(gap, mfm ? 22 : 11);
            Put(0x00, mfm ? 12 : 6);
            if (mfm)
                Put(0xA1, 3);

            foreach (byte value in dataField)
                Put(value, 1);

            Put((byte)(dataCrc >> 8), 1);
            Put((byte)dataCrc, 1);
            Put(gap, mfm ? 24 : 27);
        }

        public byte[] Build()
        {
            for (int index = 0; index < _idams.Count; index++)
            {
                _bytes[index * 2] = (byte)_idams[index];
                _bytes[index * 2 + 1] = (byte)(_idams[index] >> 8);
            }

            return _bytes;
        }

        private void Put(byte value, int count)
        {
            for (int index = 0; index < count; index++)
            {
                _bytes[_position++] = value;
                if (doubled)
                    _bytes[_position++] = value;
            }
        }
    }
}
