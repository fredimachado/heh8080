using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class HardDiskImageRoundTripTests
{
    [Fact]
    public void ClassicHeaderExportsAsAFrehdHeaderWithTheSameSectors()
    {
        byte[] classic = Hdv(
            version: 0x10,
            headsByte: 0,
            cylinderHigh: 0xAB,
            cylinderLow: 2,
            sectorsPerCylinder: 64,
            sectorCount: 2 * 2 * 32,
            sectorZero: 0x41,
            protect: true);
        classic[10] = 0x77;

        HardDiskImage image = HardDiskImage.Parse(classic);

        Assert.Equal(new HardDiskGeometry(2, 2, 32), image.Geometry);
        Assert.True(image.WriteProtected);
        Assert.False(image.Modified);
        Assert.True(image.Geometry.Contains(0, 0, 0));
        Assert.False(image.Geometry.Contains(0, 0, 32));

        byte[] exported = image.Export();
        Assert.Equal(classic.Length, exported.Length);
        Assert.Equal((byte)0x56, exported[0]);
        Assert.Equal((byte)0xCB, exported[1]);
        Assert.Equal((byte)0x10, exported[2]);
        Assert.Equal((byte)0x80, exported[7]);
        Assert.Equal((byte)0, exported[10]);
        Assert.Equal((byte)2, exported[26]);
        Assert.Equal((byte)0, exported[27]);
        Assert.Equal((byte)2, exported[28]);
        Assert.Equal((byte)64, exported[29]);
        Assert.Equal(classic.AsSpan(256).ToArray(), exported.AsSpan(256).ToArray());

        byte[] fullCylinder = Hdv(
            version: 0x11,
            headsByte: 0,
            cylinderHigh: 0xFF,
            cylinderLow: 0,
            sectorsPerCylinder: 32,
            sectorCount: 256 * 1 * 32,
            sectorZero: 0x5A);
        HardDiskImage wide = HardDiskImage.Parse(fullCylinder);
        Assert.Equal(new HardDiskGeometry(256, 1, 32), wide.Geometry);
        byte[] wideExport = wide.Export();
        Assert.Equal((byte)1, wideExport[26]);
        Assert.Equal((byte)1, wideExport[27]);
        Assert.Equal((byte)0, wideExport[28]);
        Assert.Equal((byte)32, wideExport[29]);
        Assert.Equal((byte)0x5A, wideExport[256]);
        Assert.Equal(fullCylinder.AsSpan(256).ToArray(), wideExport.AsSpan(256).ToArray());

        byte[] eightHeads = Hdv(
            version: 0x10,
            headsByte: 0,
            cylinderHigh: 0,
            cylinderLow: 1,
            sectorsPerCylinder: 0,
            sectorCount: 1 * 8 * 32,
            sectorZero: 0x19);
        HardDiskImage packed = HardDiskImage.Parse(eightHeads);
        Assert.Equal(new HardDiskGeometry(1, 8, 32), packed.Geometry);
        Assert.Equal((byte)0, packed.Export()[29]);
    }

    [Fact]
    public void FrehdHeaderRoundTrips()
    {
        byte[] source = Hdv(
            version: 0x11,
            headsByte: 2,
            cylinderHigh: 1,
            cylinderLow: 0x2C,
            sectorsPerCylinder: 4,
            sectorCount: 300 * 2 * 2,
            sectorZero: 0x64,
            protect: true);

        HardDiskImage image = HardDiskImage.Parse(source);

        Assert.Equal(new HardDiskGeometry(300, 2, 2), image.Geometry);
        Assert.True(image.WriteProtected);
        Assert.Equal(source, image.Export());

        byte[] open = Hdv(
            version: 0x10,
            headsByte: 1,
            cylinderHigh: 0,
            cylinderLow: 1,
            sectorsPerCylinder: 0,
            sectorCount: 256,
            sectorZero: 0x01);
        HardDiskImage hostProtected = HardDiskImage.Parse(open, hostWriteProtected: true);
        Assert.Equal(new HardDiskGeometry(1, 1, 256), hostProtected.Geometry);
        Assert.True(hostProtected.WriteProtected);
        byte[] protectedExport = hostProtected.Export();
        Assert.Equal((byte)0x80, protectedExport[7]);
        Assert.Equal((byte)1, protectedExport[26]);
        Assert.Equal((byte)0, protectedExport[29]);
        Assert.Equal(open.AsSpan(256).ToArray(), protectedExport.AsSpan(256).ToArray());

        byte[] masked = Hdv(
            version: 0x10,
            headsByte: 1,
            cylinderHigh: 0xFF,
            cylinderLow: 0xFF,
            sectorsPerCylinder: 1,
            sectorCount: 1023,
            sectorZero: 0x02);
        HardDiskImage highBits = HardDiskImage.Parse(masked);
        Assert.Equal(new HardDiskGeometry(1023, 1, 1), highBits.Geometry);
        Assert.Equal((byte)0x03, highBits.Export()[27]);
    }

    [Fact]
    public void RawBodyRoundTripsWhenGeometryIsPassed()
    {
        var geometry = new HardDiskGeometry(2, 3, 4);
        var body = new byte[geometry.SectorCount * HardDiskGeometry.BytesPerSector];
        body[0] = 0x11;
        body[1] = 0x22;
        body[^1] = 0x33;

        HardDiskImage image = HardDiskImage.Parse(body, geometry, hostWriteProtected: true);

        Assert.Equal(geometry, image.Geometry);
        Assert.True(image.WriteProtected);
        Assert.Equal(body, image.Export());
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(body));
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(body, hostWriteProtected: true));
    }

    [Fact]
    public void SectorZeroIsFileOffset256()
    {
        byte[] source = Hdv(
            version: 0x10,
            headsByte: 1,
            cylinderHigh: 0,
            cylinderLow: 1,
            sectorsPerCylinder: 1,
            sectorCount: 1,
            sectorZero: 0x10);
        source[257] = 0x20;
        source[258] = 0x30;

        byte[] exported = HardDiskImage.Parse(source).Export();

        Assert.Equal((byte)0x56, exported[0]);
        Assert.Equal((byte)0xCB, exported[1]);
        Assert.Equal((byte)0x10, exported[256]);
        Assert.Equal((byte)0x20, exported[257]);
        Assert.Equal((byte)0x30, exported[258]);
        Assert.NotEqual(exported[0], exported[256]);
    }

    [Fact]
    public void TruncatedBodyIsRejected()
    {
        byte[] headerOnly = Hdv(
            version: 0x10,
            headsByte: 1,
            cylinderHigh: 0,
            cylinderLow: 1,
            sectorsPerCylinder: 1,
            sectorCount: 1,
            sectorZero: 1);
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(headerOnly[..256]));
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(headerOnly[..^1]));
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse([0x56, 0xCB]));

        var geometry = new HardDiskGeometry(1, 1, 1);
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(new byte[255], geometry));
    }

    [Fact]
    public void BadMagicOrVersionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse([0x56, 0x00]));
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse([0x00, 0xCB]));
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse([]));

        byte[] badVersion = Hdv(
            version: 0x12,
            headsByte: 1,
            cylinderHigh: 0,
            cylinderLow: 1,
            sectorsPerCylinder: 1,
            sectorCount: 1,
            sectorZero: 1);
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(badVersion));

        badVersion[2] = 0x00;
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(badVersion));
    }

    [Fact]
    public void UndividedGeometryIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(Header(
            version: 0x10, headsByte: 0, cylinderHigh: 0, cylinderLow: 1, sectorsPerCylinder: 33)));
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(Header(
            version: 0x10, headsByte: 3, cylinderHigh: 0, cylinderLow: 1, sectorsPerCylinder: 32)));
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(Header(
            version: 0x11, headsByte: 1, cylinderHigh: 0, cylinderLow: 0, sectorsPerCylinder: 1)));
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(Header(
            version: 0x10, headsByte: 9, cylinderHigh: 0, cylinderLow: 1, sectorsPerCylinder: 9)));
    }

    [Fact]
    public void TrailingBytesAreRejected()
    {
        byte[] hdv = Hdv(
            version: 0x10,
            headsByte: 1,
            cylinderHigh: 0,
            cylinderLow: 1,
            sectorsPerCylinder: 1,
            sectorCount: 1,
            sectorZero: 1);
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse([.. hdv, 0xFF]));

        var geometry = new HardDiskGeometry(1, 1, 1);
        var raw = new byte[HardDiskGeometry.BytesPerSector + 1];
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(raw, geometry));
    }

    [Fact]
    public void MagicPlusACallerGeometryIsRejected()
    {
        var geometry = new HardDiskGeometry(1, 1, 1);
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse([0x56, 0xCB], geometry));

        byte[] hdv = Hdv(
            version: 0x10,
            headsByte: 1,
            cylinderHigh: 0,
            cylinderLow: 1,
            sectorsPerCylinder: 1,
            sectorCount: 1,
            sectorZero: 1);
        Assert.Throws<InvalidDataException>(() => HardDiskImage.Parse(hdv, geometry));
    }

    private static byte[] Header(byte version, byte headsByte, byte cylinderHigh, byte cylinderLow, byte sectorsPerCylinder) =>
        Hdv(version, headsByte, cylinderHigh, cylinderLow, sectorsPerCylinder, sectorCount: 0, sectorZero: 0);

    private static byte[] Hdv(
        byte version,
        byte headsByte,
        byte cylinderHigh,
        byte cylinderLow,
        byte sectorsPerCylinder,
        int sectorCount,
        byte sectorZero,
        bool protect = false)
    {
        var file = new byte[256 + (sectorCount * HardDiskGeometry.BytesPerSector)];
        file[0] = 0x56;
        file[1] = 0xCB;
        file[2] = version;
        if (protect)
            file[7] = 0x80;

        file[26] = headsByte;
        file[27] = cylinderHigh;
        file[28] = cylinderLow;
        file[29] = sectorsPerCylinder;
        if (sectorCount > 0)
        {
            file[256] = sectorZero;
            file[^1] = 0xEE;
        }

        return file;
    }
}
