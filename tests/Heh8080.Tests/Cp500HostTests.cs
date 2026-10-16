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

public sealed class Cp500CommandTests
{
    [Fact]
    public void ReadyWithinOneSecondExitsMatched()
    {
        using var dir = new TempRom();
        Cp500Command.Batch batch = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", dir.RomPath,
            "--until-screen", "READY",
            "--timeout", "1s",
            "--dump-screen",
        ]));

        using Cp500Session session = Cp500Files.Load(batch.Files);
        Cp500BatchResult result = batch.Run(session);

        Assert.True(result.Matched);
        Assert.Contains("READY", result.Screen);
        Assert.Equal(Cp500Exit.Success, Cp500Exit.For(result));
    }

    [Fact]
    public void AMissedSnippetExitsTimeoutAndStillShowsReady()
    {
        using var dir = new TempRom();
        Cp500Command.Batch batch = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", dir.RomPath,
            "--until-screen", "NOPE",
            "--timeout", "50ms",
            "--dump-screen",
        ]));

        using Cp500Session session = Cp500Files.Load(batch.Files);
        Cp500BatchResult result = batch.Run(session);

        Assert.False(result.Matched);
        Assert.Equal(Cp500Exit.Timeout, Cp500Exit.For(result));
        Assert.Contains("READY", result.Screen);
    }

    [Fact]
    public void Disk2InstallsThreeDrives()
    {
        using var dir = new TempRom();
        string disk = Path.Combine(dir.Path, "disk2.dsk");
        File.WriteAllBytes(disk, new byte[ModelIIIDiskGeometry.DiskSizeBytes]);
        Cp500Command.Batch batch = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", dir.RomPath,
            "--disk2", disk,
        ]));

        using Cp500Session session = Cp500Files.Load(batch.Files);

        Assert.Equal(3, session.InstalledDrives);
        Assert.True(session.IsDriveMounted(2));
        session.Poke(0xC000, [0xA5]);
        Assert.Equal(new byte[] { 0xA5 }, session.Peek(0xC000, 1));
    }

    [Fact]
    public void ADrivePastMaxDrivesIsRejected()
    {
        Cp500Rejected ex = Assert.Throws<Cp500Rejected>(() =>
            Cp500Command.Parse(["--rom", "stub.rom", "--disk4", "disk.dsk"]));

        Assert.Equal(
            $"Drive {ModelIIIDiskGeometry.MaxDrives} is outside 0..{ModelIIIDiskGeometry.MaxDrives - 1}.",
            ex.Message);
    }

    [Fact]
    public void Rs232IsOptionalAndABadLinkIsRejected()
    {
        Cp500Command.Batch plain = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse(["--rom", "stub.rom"]));
        Assert.Same(Rs232Link.Off, plain.Rs232);

        Cp500Command.Batch tcp = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", "stub.rom",
            "--rs232", "tcp:Printer.local:2323",
        ]));
        Assert.Equal("Printer.local", Assert.IsType<Rs232Link.TcpLink>(tcp.Rs232).Host);

        Cp500Command.Serve serve = Assert.IsType<Cp500Command.Serve>(Cp500Command.Parse([
            "--mcp", "--rom", "stub.rom", "--rs232", "com:COM3",
        ]));
        Assert.Equal("COM3", Assert.IsType<Rs232Link.ComLink>(serve.Rs232).Name);

        Cp500Rejected bad = Assert.Throws<Cp500Rejected>(() =>
            Cp500Command.Parse(["--rom", "stub.rom", "--rs232", "tcp:a:b:1"]));
        Assert.Contains(Rs232Link.Syntax, bad.Message);

        Cp500Rejected duplicate = Assert.Throws<Cp500Rejected>(() =>
            Cp500Command.Parse(["--rom", "stub.rom", "--rs232", "off", "--rs232", "off"]));
        Assert.Equal("Duplicate flag --rs232.", duplicate.Message);
    }

    [Fact]
    public void UntilScreenWithoutTimeoutIsRejected()
    {
        Cp500Rejected ex = Assert.Throws<Cp500Rejected>(() =>
            Cp500Command.Parse(["--rom", "stub.rom", "--until-screen", "READY"]));

        Assert.Equal("--until-screen and --timeout must be passed together.", ex.Message);
    }

    [Fact]
    public void McpCombinedWithTypeIsRejected()
    {
        Cp500Rejected ex = Assert.Throws<Cp500Rejected>(() =>
            Cp500Command.Parse(["--mcp", "--rom", "stub.rom", "--type", "A"]));

        Assert.Equal(
            "--mcp cannot be combined with --type, --until-screen, --timeout, or --dump-screen.",
            ex.Message);
    }

    [Fact]
    public void HardDiskIsOffUntilRequestedAndChsNeedsTheFile()
    {
        Cp500Command.Batch plain = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse(["--rom", "stub.rom"]));
        Assert.Null(plain.Files.HardDisk);

        Cp500Rejected missing = Assert.Throws<Cp500Rejected>(() =>
            Cp500Command.Parse(["--rom", "stub.rom", "--chs", "1,1,1"]));
        Assert.Equal("--chs requires --hard-disk.", missing.Message);

        Cp500Rejected shape = Assert.Throws<Cp500Rejected>(() =>
            Cp500Command.Parse(["--rom", "stub.rom", "--hard-disk", "disk.hdv", "--chs", "1,1"]));
        Assert.Equal("--chs needs <cylinders>,<heads>,<sectors>.", shape.Message);

        Cp500Rejected range = Assert.Throws<Cp500Rejected>(() =>
            Cp500Command.Parse(["--rom", "stub.rom", "--hard-disk", "disk.hdv", "--chs", "0,1,1"]));
        Assert.Equal("--chs needs cylinders 1-1023, heads 1-8, and sectors 1-256.", range.Message);

        Cp500Command.Serve serve = Assert.IsType<Cp500Command.Serve>(Cp500Command.Parse([
            "--mcp", "--rom", "stub.rom", "--hard-disk", "disk.hdv", "--chs", "2,1,4",
        ]));
        Assert.Equal("disk.hdv", serve.Files.HardDisk!.Value.Path);
        Assert.Equal(new HardDiskGeometry(2, 1, 4), serve.Files.HardDisk.Value.RawGeometry);
    }

    [Fact]
    public void TheSessionMountsItsOwnHardDiskAndWritesTheLoadedFormat()
    {
        using var dir = new TempRom();
        string statusRom = System.IO.Path.Combine(dir.Path, "status.rom");
        string writeRom = System.IO.Path.Combine(dir.Path, "write.rom");
        string image = System.IO.Path.Combine(dir.Path, "disk.hdv");
        string raw = System.IO.Path.Combine(dir.Path, "body.img");
        string floppy = System.IO.Path.Combine(dir.Path, "disk2.dsk");
        File.WriteAllBytes(statusRom, [0xDB, 0xCF, 0x32, 0x00, 0x40, 0x18, 0xFE]);
        File.WriteAllBytes(writeRom, WriteSectorOf5A);
        File.WriteAllBytes(floppy, new byte[ModelIIIDiskGeometry.DiskSizeBytes]);

        Cp500Command.Batch off = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse(["--rom", statusRom]));
        using (Cp500Session quiet = Cp500Files.Load(off.Files))
            Assert.Equal(new byte[] { 0xFF }, quiet.Peek(0x4000, 1));

        byte[] original = OneSector(0x11);
        File.WriteAllBytes(image, original);
        Cp500Command.Batch on = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", statusRom, "--disk2", floppy, "--hard-disk", image,
        ]));
        using (Cp500Session session = Cp500Files.Load(on.Files))
        {
            Assert.Equal(3, session.InstalledDrives);
            Assert.True(session.IsDriveMounted(2));
            Assert.False(session.IsDriveMounted(0));
            Assert.Equal(new byte[] { 0x40 }, session.Peek(0x4000, 1));
        }

        Assert.Equal(original, File.ReadAllBytes(image));

        Cp500Command.Batch writer = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", writeRom, "--hard-disk", image,
        ]));
        using (Cp500Session session = Cp500Files.Load(writer.Files))
            Assert.Equal(2, session.InstalledDrives);

        byte[] saved = File.ReadAllBytes(image);
        Assert.Equal(0x56, saved[0]);
        Assert.Equal(0xCB, saved[1]);
        Assert.All(saved.AsSpan(256).ToArray(), value => Assert.Equal(0x5A, value));

        byte[] locked = OneSector(0x11);
        locked[7] = 0x80;
        File.WriteAllBytes(image, locked);
        using (Cp500Files.Load(writer.Files))
        {
        }

        Assert.Equal(locked, File.ReadAllBytes(image));

        File.WriteAllBytes(raw, Enumerable.Repeat((byte)0x11, 256).ToArray());
        Cp500Command.Batch rawWriter = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", writeRom, "--hard-disk", raw, "--chs", "1,1,1",
        ]));
        using (Cp500Files.Load(rawWriter.Files))
        {
        }

        Assert.Equal(Enumerable.Repeat((byte)0x5A, 256).ToArray(), File.ReadAllBytes(raw));

        File.WriteAllBytes(image, [0x56, 0xCB]);
        Cp500Rejected bad = Assert.Throws<Cp500Rejected>(() => Cp500Files.Load(writer.Files));
        Assert.Contains("not a valid image", bad.Message);

        Cp500Command.Batch magicAndGeometry = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", statusRom, "--hard-disk", image, "--chs", "1,1,1",
        ]));
        Cp500Rejected rejectedGeometry = Assert.Throws<Cp500Rejected>(() => Cp500Files.Load(magicAndGeometry.Files));
        Assert.Contains("not a valid image", rejectedGeometry.Message);
    }

    [Fact]
    public void AnUnmappedCharacterIsExit2AndDoesNotChangeTheScreen()
    {
        using var dir = new TempRom();
        Cp500Command.Batch batch = Assert.IsType<Cp500Command.Batch>(Cp500Command.Parse([
            "--rom", dir.RomPath,
            "--type", "\\",
            "--dump-screen",
        ]));
        using Cp500Session session = Cp500Files.Load(batch.Files);
        string before = session.ReadScreen();

        int code;
        try
        {
            batch.Run(session);
            code = Cp500Exit.For(new Cp500BatchResult(true, ""));
        }
        catch (Cp500Rejected)
        {
            code = Cp500Exit.Rejected;
        }

        Assert.Equal(before, session.ReadScreen());
        Assert.Equal(2, code);
    }

    private static byte[] OneSector(byte marker)
    {
        var file = new byte[512];
        file[0] = 0x56;
        file[1] = 0xCB;
        file[2] = 0x10;
        file[26] = 1;
        file[28] = 1;
        file[29] = 1;
        file[256] = marker;
        return file;
    }

    private static readonly byte[] WriteSectorOf5A =
    [
        0xF3,
        0x3E, 0x00,
        0xD3, 0xCE,
        0x3E, 0x30,
        0xD3, 0xCF,
        0x06, 0x00,
        0x3E, 0x5A,
        0x0E, 0xC8,
        0xED, 0x79,
        0x10, 0xFC,
        0x18, 0xFE,
    ];

    private sealed class TempRom : IDisposable
    {
        public TempRom()
        {
            Path = Directory.CreateTempSubdirectory("heh8080-cp500").FullName;
            RomPath = System.IO.Path.Combine(Path, "stub.rom");
            File.WriteAllBytes(RomPath, ReadyStub);
        }

        public string Path { get; }
        public string RomPath { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);

        private static readonly byte[] ReadyStub =
        [
            0x21, 0x0D, 0x00,
            0x11, 0x00, 0x3C,
            0x01, 0x05, 0x00,
            0xED, 0xB0,
            0x18, 0xFE,
            0x52, 0x45, 0x41, 0x44, 0x59,
        ];
    }
}
