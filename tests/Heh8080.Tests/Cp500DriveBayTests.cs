using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500DriveBayTests
{
    private const int RawSingleSidedBytes = ModelIIIDiskGeometry.DiskSizeBytes;

    private readonly Cp500DriveBay _bay = new();
    private readonly List<byte[]> _saved = [];

    private static byte[] Payload(byte value) => Enumerable.Repeat(value, ModelIIIDiskGeometry.BytesPerSector).ToArray();

    private static byte[] Jv3Image() =>
        ModelIIIDiskCodec.Serialize(ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.SingleSided), ModelIIIDiskImageKind.Jv3);

    private bool Write(int drive, byte value) => _bay.Images.WriteSector(drive, track: 0, side: 0, sector: 1, Payload(value));

    private static byte[] FirstSector(byte[] image, ModelIIIDiskImageKind kind) =>
        ModelIIIDiskCodec.Parse(image, kind).FindSector(0, 0, 1)!.Data;

    [Theory]
    [InlineData("disk.dmk", 12345, ModelIIIDiskImageKind.Dmk)]
    [InlineData("DISK.DMK", ModelIIIDiskGeometry.DiskSizeBytes, ModelIIIDiskImageKind.Dmk)]
    [InlineData("disk.jv3", ModelIIIDiskGeometry.DiskSizeBytes, ModelIIIDiskImageKind.Jv3)]
    [InlineData("disk.dsk", 193024, ModelIIIDiskImageKind.Jv3)]
    [InlineData("disk.img", 193024, ModelIIIDiskImageKind.Jv3)]
    public void TheExtensionNamesTheContainerAndAnythingElseIsJv3(string fileName, int length, ModelIIIDiskImageKind expected)
    {
        Assert.Equal(expected, ModelIIIDiskCodec.DetectKind(fileName, length));
    }

    [Theory]
    [InlineData(ModelIIIDiskGeometry.DiskSizeBytes)]
    [InlineData(ModelIIIDiskGeometry.DoubleSidedDiskSizeBytes)]
    public void ADskFileOfExactlyRawSizeIsRaw(int length)
    {
        Assert.Equal(ModelIIIDiskImageKind.Raw, ModelIIIDiskCodec.DetectKind("disk.dsk", length));
        Assert.Equal(ModelIIIDiskImageKind.Raw, ModelIIIDiskCodec.DetectKind("disk.DSK", length));
        Assert.Equal(ModelIIIDiskImageKind.Jv3, ModelIIIDiskCodec.DetectKind("disk.dsk", length + 1));
    }

    [Fact]
    public void InsertMountsTheImageAndRemembersItsName()
    {
        _bay.Insert(1, new byte[RawSingleSidedBytes], "boot.dsk", saved => _saved.Add(saved));

        Assert.True(_bay.Images.IsMounted(1));
        Assert.Equal("boot.dsk", _bay.NameOf(1));
        Assert.Null(_bay.NameOf(0));
    }

    [Fact]
    public void InsertRejectsAnImageThatIsNotADiskAndLeavesTheDriveEmpty()
    {
        Assert.Throws<InvalidDataException>(() => _bay.Insert(0, new byte[100], "junk.jv3", null));

        Assert.False(_bay.Images.IsMounted(0));
        Assert.Null(_bay.NameOf(0));
    }

    [Fact]
    public void EjectingAnUnmodifiedDiskDoesNotRewriteTheHostFile()
    {
        _bay.Insert(0, Jv3Image(), "clean.jv3", saved => _saved.Add(saved));

        _bay.Eject(0);

        Assert.Empty(_saved);
        Assert.False(_bay.Images.IsMounted(0));
        Assert.Null(_bay.NameOf(0));
    }

    [Theory]
    [InlineData(ModelIIIDiskImageKind.Raw)]
    [InlineData(ModelIIIDiskImageKind.Jv3)]
    [InlineData(ModelIIIDiskImageKind.Dmk)]
    public void EjectingAModifiedDiskWritesItBackInItsSourceFormat(ModelIIIDiskImageKind kind)
    {
        byte[] source = ModelIIIDiskCodec.Serialize(ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.SingleSided), kind);
        string name = kind == ModelIIIDiskImageKind.Raw ? "d.dsk" : $"d.{kind.ToString().ToLowerInvariant()}";
        _bay.Insert(0, source, name, saved => _saved.Add(saved));
        Assert.True(Write(0, 0x5A));

        _bay.Eject(0);

        byte[] written = Assert.Single(_saved);
        Assert.Equal(Payload(0x5A), FirstSector(written, kind));
        Assert.False(_bay.Images.IsMounted(0));
    }

    [Fact]
    public void ReadOnlyDisksRefuseWritesAndAreNeverWrittenBack()
    {
        _bay.Insert(0, new byte[RawSingleSidedBytes], "locked.dsk", saved => _saved.Add(saved), readOnly: true);

        Assert.False(Write(0, 0x5A));
        _bay.Eject(0);

        Assert.Empty(_saved);
    }

    [Fact]
    public void WriteProtectedContainersAreNeverWrittenBack()
    {
        byte[] dmk = ModelIIIDiskCodec.Serialize(
            ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.SingleSided), ModelIIIDiskImageKind.Dmk);
        dmk[0] = 0xFF;
        _bay.Insert(0, dmk, "protected.dmk", saved => _saved.Add(saved));

        Assert.True(_bay.Images.IsReadOnly(0));
        _bay.Eject(0);

        Assert.Empty(_saved);
    }

    [Fact]
    public void FlushWritesEveryModifiedDiskOnceAndKeepsThemMounted()
    {
        _bay.Insert(0, new byte[RawSingleSidedBytes], "a.dsk", saved => _saved.Add(saved));
        _bay.Insert(1, new byte[RawSingleSidedBytes], "b.dsk", saved => _saved.Add(saved));
        _bay.Insert(2, new byte[RawSingleSidedBytes], "c.dsk", saved => _saved.Add(saved));
        Assert.True(Write(0, 1));
        Assert.True(Write(2, 3));

        _bay.Flush();
        _bay.Flush();

        Assert.Equal(2, _saved.Count);
        Assert.True(_bay.Images.IsMounted(0));
        Assert.True(_bay.Images.IsMounted(2));

        Assert.True(Write(0, 9));
        _bay.Flush();

        Assert.Equal(3, _saved.Count);
    }

    [Fact]
    public void ADiskInsertedWithoutAWriteBackIsSimplyDroppedOnEject()
    {
        _bay.Insert(0, new byte[RawSingleSidedBytes], "memory.dsk", writeBack: null);
        Assert.True(Write(0, 1));

        _bay.Eject(0);

        Assert.False(_bay.Images.IsMounted(0));
    }

    [Fact]
    public void AFailedWriteBackKeepsTheDiskMountedAndModified()
    {
        bool fail = true;
        _bay.Insert(0, new byte[RawSingleSidedBytes], "a.dsk", saved =>
        {
            if (fail)
                throw new IOException("disk full");

            _saved.Add(saved);
        });
        Assert.True(Write(0, 7));

        Assert.Throws<IOException>(() => _bay.Eject(0));
        Assert.True(_bay.Images.IsMounted(0));
        Assert.Equal("a.dsk", _bay.NameOf(0));

        fail = false;
        _bay.Eject(0);

        Assert.Single(_saved);
        Assert.False(_bay.Images.IsMounted(0));
    }

    [Fact]
    public void InsertingOverAMountedDiskWritesTheOldOneBackFirst()
    {
        _bay.Insert(0, new byte[RawSingleSidedBytes], "old.dsk", saved => _saved.Add(saved));
        Assert.True(Write(0, 0x11));

        _bay.Insert(0, new byte[RawSingleSidedBytes], "new.dsk", writeBack: null);

        Assert.Equal(Payload(0x11), FirstSector(Assert.Single(_saved), ModelIIIDiskImageKind.Raw));
        Assert.Equal("new.dsk", _bay.NameOf(0));
    }

    [Fact]
    public void AFailedWriteBackDuringInsertLeavesTheOldDiskInPlace()
    {
        _bay.Insert(0, new byte[RawSingleSidedBytes], "old.dsk", _ => throw new IOException("denied"));
        Assert.True(Write(0, 0x11));

        Assert.Throws<IOException>(() => _bay.Insert(0, new byte[RawSingleSidedBytes], "new.dsk", null));

        Assert.Equal("old.dsk", _bay.NameOf(0));
    }

    [Fact]
    public void DrivesOutsideTheControllerRangeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _bay.Insert(4, new byte[RawSingleSidedBytes], "x.dsk", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => _bay.Eject(-1));
        Assert.Null(_bay.NameOf(9));
    }
}
