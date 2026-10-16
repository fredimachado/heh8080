using Heh8080.Cp500Host;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500ScreenTests
{
    [Fact]
    public void ToTextKeepsPrintableBytesTrimsRowsAndIgnores32ColumnMode()
    {
        var video = new Cp500VideoDevice();
        Write(video, column: 0, (byte)'A');
        Write(video, column: 1, 0x01);
        Write(video, column: 2, 0x7F);
        Write(video, column: 3, (byte)'~');
        Write(video, column: 4, (byte)' ');
        Write(video, column: 5, (byte)'Z');
        for (int column = 6; column < 12; column++)
            Write(video, column, (byte)' ');

        const string expected = "A  ~ Z\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n";

        Assert.Equal(expected, Cp500Screen.ToText(video));

        video.Out(Cp500VideoDevice.ControlPort, Cp500VideoDevice.ThirtyTwoColumnBit);

        Assert.True(video.Is32ColumnMode);
        Assert.Equal(expected, Cp500Screen.ToText(video));
    }

    private static void Write(Cp500VideoDevice video, int column, byte value) =>
        video.VideoRam.Write((ushort)(Cp500VideoDevice.StartAddress + column), value);
}

public sealed class Cp500SessionTests
{
    private static readonly byte[] ReadyStub =
    [
        0x21, 0x0D, 0x00,
        0x11, 0x00, 0x3C,
        0x01, 0x05, 0x00,
        0xED, 0xB0,
        0x18, 0xFE,
        0x52, 0x45, 0x41, 0x44, 0x59,
    ];

    [Fact]
    public void RunUntilMatchesReadyWithinOneEmulatedSecond()
    {
        using Cp500Session session = Cp500Session.Create(ReadyStub, new Cp500Media());

        bool matched = session.RunUntil(ScreenText.Parse("READY"), EmulatedTime.Parse("1s", Clock));

        Assert.True(matched);
        Assert.Equal(new byte[] { 0x52, 0x45, 0x41, 0x44, 0x59 }, session.Peek(0x3C00, 5));
        Assert.Contains("READY", session.ReadScreen());
        Assert.Equal(12, session.Step(1));
    }

    [Fact]
    public void RunUntilKeepsReadyOnScreenWhenTheSnippetNeverAppears()
    {
        using Cp500Session session = Cp500Session.Create(ReadyStub, new Cp500Media());

        bool matched = session.RunUntil(ScreenText.Parse("NOPE"), EmulatedTime.Parse("50ms", Clock));

        Assert.False(matched);
        Assert.Contains("READY", session.ReadScreen());
    }

    [Fact]
    public void TypingALoneBackslashIsRejectedAndLeavesTheScreen()
    {
        using Cp500Session session = Cp500Session.Create(ReadyStub, new Cp500Media());
        string before = session.ReadScreen();

        Assert.Throws<Cp500Rejected>(() => session.Type("\\"));

        Assert.Equal(before, session.ReadScreen());
        Assert.Contains("READY", session.ReadScreen());
    }

    [Fact]
    public void Disk3InstallsFourDrivesAndStaysMounted()
    {
        var media = new Cp500Media();
        media.Set(3, new Cp500Disk(BlankDisk(), "disk3.dsk", null));
        using Cp500Session session = Cp500Session.Create(ReadyStub, media);

        Assert.Equal(4, media.InstalledDrives);
        Assert.Equal(4, session.InstalledDrives);
        Assert.True(session.IsDriveMounted(3));
        Assert.False(session.IsDriveMounted(0));
        session.Poke(0xC000, [0xA5]);
        Assert.Equal(new byte[] { 0xA5 }, session.Peek(0xC000, 1));
        Assert.Equal(new byte[] { 0x21 }, session.Peek(0x0000, 1));
        session.Poke(0x0000, [0x00]);
        Assert.Equal(new byte[] { 0x21 }, session.Peek(0x0000, 1));
    }

    [Fact]
    public void Disk0AloneKeepsTwoInstalledDrives()
    {
        var media = new Cp500Media();
        media.Set(0, new Cp500Disk(BlankDisk(), "disk0.dsk", null));
        using Cp500Session session = Cp500Session.Create(ReadyStub, media);

        Assert.Equal(2, media.InstalledDrives);
        Assert.Equal(2, session.InstalledDrives);
        Assert.True(session.IsDriveMounted(0));
        Assert.False(session.IsDriveMounted(1));
        Assert.True(session.RunUntil(ScreenText.Parse("READY"), EmulatedTime.Parse("1s", Clock)));
        Assert.True(session.IsDriveMounted(0));
    }

    [Fact]
    public void ADrivePastMaxDrivesIsRejected()
    {
        var media = new Cp500Media();

        Assert.Throws<Cp500Rejected>(() => Cp500Media.EnsureInstalled(ModelIIIDiskGeometry.MaxDrives));
        Assert.Throws<Cp500Rejected>(() => media.Set(ModelIIIDiskGeometry.MaxDrives, new Cp500Disk(BlankDisk(), "disk4.dsk", null)));
    }

    [Fact]
    public void EmptyRomOverlongRomAndBadDiskAreRejected()
    {
        Assert.Throws<Cp500Rejected>(() => Cp500Session.Create(ReadOnlyMemory<byte>.Empty, new Cp500Media()));
        Assert.Throws<Cp500Rejected>(() =>
            Cp500Session.Create(new byte[Cp500MachineProfile.SystemRomSizeBytes + 1], new Cp500Media()));

        var media = new Cp500Media();
        media.Set(0, new Cp500Disk(new byte[100], "bad.dsk", null));
        Assert.Throws<Cp500Rejected>(() => Cp500Session.Create(ReadyStub, media));
    }

    [Fact]
    public void TimeoutsUseTheMachineClockAndMemoryRangesAreClosed()
    {
        long clock = Clock;
        Assert.Equal(clock, EmulatedTime.Parse("1s", clock).Cycles);
        Assert.Equal(clock * 30, EmulatedTime.Parse("30s", clock).Cycles);
        Assert.Equal(clock / 20, EmulatedTime.Parse("50ms", clock).Cycles);
        Assert.Throws<Cp500Rejected>(() => EmulatedTime.Parse("0s", clock));
        Assert.Throws<Cp500Rejected>(() => EmulatedTime.Parse("30", clock));
        Assert.Throws<Cp500Rejected>(() => ScreenText.Parse(""));

        Cp500Session session = Cp500Session.Create(ReadyStub, new Cp500Media());
        try
        {
            Assert.Throws<Cp500Rejected>(() => session.Peek(0xFFFF, 2));
            Assert.Throws<Cp500Rejected>(() => session.Poke(0xFFFF, [1, 2]));
            Assert.Throws<Cp500Rejected>(() => session.Step(0));
        }
        finally
        {
            session.Dispose();
        }

        Assert.Throws<ObjectDisposedException>(() => session.ReadScreen());
        Assert.Throws<ObjectDisposedException>(() => session.Dispose());
    }

    private static long Clock => Cp500MachineProfile.Original.ClockHz;

    private static byte[] BlankDisk() => new byte[ModelIIIDiskGeometry.DiskSizeBytes];
}
