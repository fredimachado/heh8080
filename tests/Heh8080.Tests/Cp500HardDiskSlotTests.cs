using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500HardDiskSlotTests
{
    private readonly List<byte[]> _saved = [];

    [Fact]
    public void BadImageLeavesTheMountedDiskInPlace()
    {
        var bay = new Cp500DriveBay();
        bay.HardDisk.Insert(OneSector(0x10), "good.hdv", saved => _saved.Add(saved));
        Assert.Equal(SectorResult.Ok, bay.HardDisk.Image!.WriteSector(0, 0, 0, Sector(0xAB)));

        Assert.Throws<InvalidDataException>(() => bay.HardDisk.Insert(new byte[10], "junk.hdv", null));
        Assert.Throws<InvalidDataException>(() => bay.HardDisk.Insert([0x56, 0xCB, 0x10], "short.hdv", null));
        Assert.Throws<InvalidDataException>(() =>
            bay.HardDisk.Insert(OneSector(0x99), "raw-shaped.hdv", null, rawGeometry: new HardDiskGeometry(1, 1, 1)));

        Assert.Equal("good.hdv", bay.HardDisk.Name);
        Assert.True(bay.HardDisk.Image!.Modified);
        bay.HardDisk.Flush();

        byte[] written = Assert.Single(_saved);
        Assert.Equal((byte)0xAB, written[256]);
        Assert.Equal("good.hdv", bay.HardDisk.Name);

        var empty = new Cp500DriveBay();
        Assert.Throws<InvalidDataException>(() => empty.HardDisk.Insert([0x00, 0xCB], "bad.hdv", null));
        Assert.Null(empty.HardDisk.Name);
        Assert.Null(empty.HardDisk.Image);
    }

    [Fact]
    public void FailedWriteBackLeavesTheImageMountedAndModified()
    {
        bool fail = true;
        var bay = new Cp500DriveBay();
        bay.HardDisk.Insert(OneSector(0x10), "disk.hdv", saved =>
        {
            if (fail)
                throw new IOException("disk full");

            _saved.Add(saved);
        });
        Assert.Equal(SectorResult.Ok, bay.HardDisk.Image!.WriteSector(0, 0, 0, Sector(0x7E)));

        Assert.Throws<IOException>(() => bay.HardDisk.Eject());
        Assert.Equal("disk.hdv", bay.HardDisk.Name);
        Assert.True(bay.HardDisk.Image!.Modified);
        Assert.Equal((byte)0x7E, SectorAt(bay, 0));

        Assert.Throws<IOException>(() => bay.HardDisk.Insert(OneSector(0x22), "other.hdv", null));
        Assert.Equal("disk.hdv", bay.HardDisk.Name);
        Assert.True(bay.HardDisk.Image!.Modified);

        fail = false;
        bay.HardDisk.Eject();

        byte[] written = Assert.Single(_saved);
        Assert.Equal((byte)0x7E, written[256]);
        Assert.Equal(Sector(0x7E)[^1], written[511]);
        Assert.Null(bay.HardDisk.Name);
        Assert.Null(bay.HardDisk.Image);
    }

    [Fact]
    public void EjectOfAnEmptySlotAndASecondFlushDoNothing()
    {
        var bay = new Cp500DriveBay();
        bay.HardDisk.Eject();
        bay.HardDisk.Flush();
        bay.Flush();
        Assert.Null(bay.HardDisk.Name);
        Assert.Empty(_saved);

        bay.HardDisk.Insert(OneSector(0x10), "clean.hdv", saved => _saved.Add(saved));
        bay.HardDisk.Flush();
        bay.HardDisk.Eject();
        Assert.Empty(_saved);
        Assert.Null(bay.HardDisk.Name);

        bay.HardDisk.Eject();
        Assert.Empty(_saved);

        bay.HardDisk.Insert(OneSector(0x10), "dirty.hdv", saved => _saved.Add(saved));
        Assert.Equal(SectorResult.Ok, bay.HardDisk.Image!.WriteSector(0, 0, 0, Sector(0x44)));
        bay.Flush();
        bay.Flush();
        bay.HardDisk.Flush();

        byte[] written = Assert.Single(_saved);
        Assert.Equal((byte)0x56, written[0]);
        Assert.Equal((byte)0xCB, written[1]);
        Assert.Equal((byte)0x44, written[256]);
        Assert.Equal("dirty.hdv", bay.HardDisk.Name);
        Assert.False(bay.HardDisk.Image!.Modified);

        bay.HardDisk.Insert(Sector(0x42), "body.img", saved => _saved.Add(saved), rawGeometry: new HardDiskGeometry(1, 1, 1));
        Assert.Equal(SectorResult.Ok, bay.HardDisk.Image!.WriteSector(0, 0, 0, Sector(0x99)));
        bay.HardDisk.Flush();
        bay.HardDisk.Flush();
        byte[] raw = _saved[1];
        Assert.Equal(HardDiskGeometry.BytesPerSector, raw.Length);
        Assert.Equal((byte)0x99, raw[0]);

        bay.HardDisk.Insert(OneSector(0x10), "locked.hdv", saved => _saved.Add(saved), readOnly: true);
        Assert.Equal(SectorResult.WriteProtected, bay.HardDisk.Image!.WriteSector(0, 0, 0, Sector(0x01)));
        bay.HardDisk.Eject();

        byte[] headerProtected = OneSector(0x10);
        headerProtected[7] = 0x80;
        bay.HardDisk.Insert(headerProtected, "header.hdv", saved => _saved.Add(saved));
        Assert.Equal(SectorResult.WriteProtected, bay.HardDisk.Image!.WriteSector(0, 0, 0, Sector(0x01)));
        Assert.False(bay.HardDisk.Image.Modified);
        bay.Flush();

        bay.HardDisk.Insert(OneSector(0x10), "memory.hdv", writeBack: null);
        Assert.Equal(SectorResult.Ok, bay.HardDisk.Image!.WriteSector(0, 0, 0, Sector(0x01)));
        bay.HardDisk.Eject();

        Assert.Equal(2, _saved.Count);
    }

    private static byte SectorAt(Cp500DriveBay bay, int offset)
    {
        var sector = new byte[HardDiskGeometry.BytesPerSector];
        Assert.Equal(SectorResult.Ok, bay.HardDisk.Image!.ReadSector(0, 0, 0, sector));
        return sector[offset];
    }

    private static byte[] Sector(byte value)
    {
        var data = new byte[HardDiskGeometry.BytesPerSector];
        data[0] = value;
        data[^1] = (byte)~value;
        return data;
    }

    private static byte[] OneSector(byte marker)
    {
        var file = new byte[HardDiskGeometry.BytesPerSector * 2];
        file[0] = 0x56;
        file[1] = 0xCB;
        file[2] = 0x10;
        file[26] = 1;
        file[28] = 1;
        file[29] = 1;
        file[256] = marker;
        return file;
    }
}
