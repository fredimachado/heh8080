using Heh8080.Core;
using Heh8080.Devices;
using static Heh8080.Devices.Cp500HardDiskController;

namespace Heh8080.Tests;

public sealed class Cp500HardDiskRegisterTests
{
    private const byte ReadyAndSeek = StatusReady | StatusSeekComplete;
    private const byte Drq = ReadyAndSeek | StatusDataRequest;
    private const byte BusyStatus = StatusReady | StatusBusy | StatusCommandInProgress;

    private static readonly byte[] Spin = [0x18, 0xFE];

    [Fact]
    public void StockMachineLeavesC0ThroughCfUnregistered()
    {
        var machine = new Cp500Machine(Cp500MachineProfile.Original, Spin);

        for (byte port = BasePort; port <= StatusCommandPort; port++)
        {
            machine.IoBus.Out(port, 0x00);
            Assert.Equal(0xFF, machine.IoBus.In(port));
        }

        machine.Step();
        Assert.Equal(0xFF, machine.IoBus.In(BasePort));
        Assert.False(machine.Profile.HardDiskInstalled);
        Assert.Equal(2, machine.Profile.InstalledDrives);
        Assert.False(Cp500MachineProfile.Original.HardDiskInstalled);
    }

    [Fact]
    public void ApplyIsTheOnlyWriterAndTheStockProfileStaysOff()
    {
        var profile = new Cp500MachineProfile(32, 1) { ClockHz = 800, Rs232Installed = true };
        Cp500MachineProfile applied = HardDiskAttachment.Install(new HardDiskSlot(), Clock()).Apply(profile);

        Assert.True(applied.HardDiskInstalled);
        Assert.False(profile.HardDiskInstalled);
        Assert.False(Cp500MachineProfile.Original.HardDiskInstalled);
        Assert.True(applied.Rs232Installed);
        Assert.Equal(32, applied.RamSizeKiB);
        Assert.Equal(1, applied.InstalledDrives);
        Assert.Equal(800, applied.ClockHz);
        Cp500MachineProfile cleared = HardDiskAttachment.None.Apply(applied);
        Assert.False(cleared.HardDiskInstalled);
        Assert.True(cleared.Rs232Installed);
        Assert.Throws<ArgumentNullException>(() => HardDiskAttachment.Install(null!, Clock()));
        Assert.Throws<ArgumentNullException>(() => HardDiskAttachment.Install(new HardDiskSlot(), null!));
    }

    [Fact]
    public void AttachmentMustMatchTheProfileFlag()
    {
        HardDiskAttachment board = HardDiskAttachment.Install(new HardDiskSlot(), Clock());
        Cp500MachineProfile installed = board.Apply(Cp500MachineProfile.Original);

        Assert.Throws<ArgumentException>(() => new Cp500Machine(Cp500MachineProfile.Original, Spin, hardDisk: board));
        Assert.Throws<ArgumentException>(() => new Cp500Machine(installed, Spin));
        Assert.Throws<ArgumentException>(() =>
            new Cp500Machine(Cp500MachineProfile.Original, Spin, hardDisk: new HardDiskAttachment(false, new HardDiskSlot(), null)));
        Assert.Throws<ArgumentException>(() =>
            new Cp500Machine(installed, Spin, hardDisk: new HardDiskAttachment(true, null, Clock())));
        Assert.Throws<ArgumentException>(() => new Cp500Automation(Spin, hardDisk: board));
    }

    [Fact]
    public void InstalledBoardReadsTheTaskFileAndResetKeepsTheDisk()
    {
        var bay = new Cp500DriveBay();
        bay.HardDisk.Insert(Image(1), "disk.hdv", writeBack: null);
        var serial = new Rs232Attachment(true, null);
        HardDiskAttachment board = HardDiskAttachment.Install(bay.HardDisk, Clock());
        Cp500MachineProfile profile = board.Apply(serial.Apply(Cp500MachineProfile.Original));
        var automation = new Cp500Automation(Spin, bay, profile, serial, board);
        Cp500Machine machine = automation.Machine;

        Assert.True(machine.Profile.HardDiskInstalled);
        Assert.True(machine.Profile.Rs232Installed);
        Assert.Equal(2, machine.Profile.InstalledDrives);
        Assert.NotEqual(0xFF, machine.IoBus.In(0xE8));
        Assert.Equal(StatusReady, machine.IoBus.In(StatusCommandPort));

        machine.IoBus.Out(0xCE, 0x00);
        machine.IoBus.Out(StatusCommandPort, 0x20);
        Assert.Equal(Drq, machine.IoBus.In(StatusCommandPort));
        machine.Step();
        Assert.Equal(Drq, machine.IoBus.In(StatusCommandPort));
        Assert.Equal(SectorBytes(0)[0], machine.IoBus.In(DataPort));

        machine.IoBus.Out(0xCC, 0x15);
        machine.Reset();
        Assert.Equal(0, machine.IoBus.In(0xCC));
        Assert.Equal(StatusReady, machine.IoBus.In(StatusCommandPort));
        Assert.Equal("disk.hdv", bay.HardDisk.Name);
        machine.IoBus.Out(0xCE, 0x00);
        machine.IoBus.Out(StatusCommandPort, 0x20);
        Assert.Equal(SectorBytes(0), ReadData(machine.IoBus));
    }

    [Fact]
    public void NoImageIsReadyAndASectorCommandSetsIdNotFound()
    {
        var board = new Board();

        Assert.Equal(StatusReady, board.Bus.In(StatusCommandPort));
        Assert.Equal(0, board.Bus.In(BasePort));

        board.Command(0x20);

        Assert.Equal(ErrorIdNotFound, board.Bus.In(0xC9));
        Assert.Equal((byte)(StatusReady | StatusError), board.Bus.In(StatusCommandPort));
        Assert.Equal(0, board.Bus.In(StatusCommandPort) & StatusDataRequest);
    }

    [Fact]
    public void BusyReadIsStatusAndBusyWriteIsIgnored()
    {
        var board = new Board(Image(1), delayCycles: 40);
        board.Bus.Out(0xCC, 0x15);
        board.Command(0x10);

        byte status = board.Bus.In(StatusCommandPort);
        Assert.Equal(BusyStatus, status);
        for (byte port = BasePort; port <= StatusCommandPort; port++)
            Assert.Equal(status, board.Bus.In(port));

        board.Bus.Out(0xCC, 0x09);
        board.Bus.Out(0xCB, 0x03);
        board.Command(0x70);
        board.Controller.AdvanceCycles(39);
        Assert.Equal(BusyStatus, board.Bus.In(StatusCommandPort));

        board.Controller.AdvanceCycles(1);
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
        Assert.Equal(0, board.Bus.In(0xCC));
        Assert.Equal(0, board.Bus.In(0xCB));
        Assert.Throws<ArgumentOutOfRangeException>(() => board.Controller.AdvanceCycles(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HardDiskTiming(-1));
    }

    [Fact]
    public void ZeroDelayCommitsInsideTheCommandWrite()
    {
        var board = new Board(Image(1));
        board.Bus.Out(0xCC, 0x15);
        board.Command(0x10);

        Assert.Equal(0, board.Bus.In(0xCC));
        Assert.Equal(0, board.Bus.In(StatusCommandPort) & StatusBusy);
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
    }

    [Fact]
    public void NonZeroDelayStaysBusyUntilAdvanceCycles()
    {
        var board = new Board(Image(1), delayCycles: 8);
        board.Select();
        board.Command(0x70);

        Assert.Equal((byte)(BusyStatus | StatusSeekComplete), board.Bus.In(StatusCommandPort));
        board.Controller.AdvanceCycles(7);
        Assert.Equal((byte)(BusyStatus | StatusSeekComplete), board.Bus.In(StatusCommandPort));
        board.Controller.AdvanceCycles(1);
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
    }

    [Fact]
    public void ReadSectorHoldsDrqUntilTheLastByte()
    {
        var board = new Board(Image(1));
        board.Bus.Out(0xCA, 4);
        board.Bus.Out(0xC3, 0);
        board.Select(sector: 0);
        board.Command(0x24);

        Assert.Equal(Drq, board.Bus.In(StatusCommandPort));
        Assert.Equal(0, board.Bus.In(StatusCommandPort) & (StatusBusy | StatusCommandInProgress | StatusWriteFault | StatusCorrected));
        Assert.Equal(0, board.Bus.In(0xCB));
        Assert.Equal(4, board.Bus.In(0xCA));
        Assert.Equal(0, board.Bus.In(0xC3));

        byte[] expected = SectorBytes(0);
        for (int i = 0; i < 255; i++)
        {
            Assert.Equal(expected[i], board.Bus.In(DataPort));
            Assert.Equal(Drq, board.Bus.In(StatusCommandPort));
        }

        Assert.Equal(expected[255], board.Bus.In(DataPort));
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
        Assert.Equal(0, board.Bus.In(0xC9));
    }

    [Fact]
    public void WriteSectorCommitsOnTheLastByteAndAPartialWriteDoesNotTouchTheImage()
    {
        var board = new Board(Image(2));
        byte[] original = SectorBytes(0);
        board.Select();
        board.Command(0x30);
        Assert.Equal(Drq, board.Bus.In(StatusCommandPort));
        for (int i = 0; i < 10; i++)
            board.Bus.Out(DataPort, 0xFF);

        board.Command(0x10);
        board.Select();
        board.Command(0x20);
        Assert.Equal(original, board.ReadData());

        board.Select(sector: 5);
        board.Command(0x30);
        board.WriteData(Enumerable.Repeat((byte)0xA5, 256).ToArray());
        Assert.Equal(ErrorIdNotFound, board.Bus.In(0xC9));
        Assert.Equal(0, board.Bus.In(StatusCommandPort) & StatusDataRequest);

        board.Select();
        board.Command(0x20);
        Assert.Equal(original, board.ReadData());

        byte[] replacement = Enumerable.Repeat((byte)0x5A, 256).ToArray();
        replacement[0] = 0x11;
        replacement[255] = 0x22;
        board.Select();
        board.Command(0x30);
        for (int i = 0; i < 255; i++)
        {
            board.Bus.Out(DataPort, replacement[i]);
            Assert.Equal(Drq, board.Bus.In(StatusCommandPort));
        }

        board.Bus.Out(DataPort, replacement[255]);
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
        Assert.Equal(0, board.Bus.In(0xC9));
        board.Command(0x20);
        Assert.Equal(replacement, board.ReadData());
    }

    [Fact]
    public void MultiSectorWriteAndANon256SizeAbort()
    {
        var board = new Board(Image(1));
        byte[] original = SectorBytes(0);
        board.Select();
        board.Command(0x34);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC9));
        Assert.Equal((byte)(ReadyAndSeek | StatusError), board.Bus.In(StatusCommandPort));

        board.Bus.Out(0xCE, 0x40);
        board.Command(0x20);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC9));
        board.Bus.Out(0xCE, 0x60);
        board.Command(0x30);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC9));
        board.Bus.Out(0xCE, 0x20);
        board.Command(0x40);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC9));
        Assert.Equal(0, board.Bus.In(StatusCommandPort) & StatusDataRequest);

        board.Select();
        board.Command(0x20);
        Assert.Equal(original, board.ReadData());
    }

    [Fact]
    public void Drives1Through3MatchAMissingImage()
    {
        var present = new Board(Image(1));
        var missing = new Board();
        foreach (byte drive in new byte[] { 1, 2, 3 })
        {
            byte sdh = (byte)(drive << 3);
            present.Select(sdh: sdh);
            missing.Select(sdh: sdh);
            present.Command(0x20);
            missing.Command(0x20);
            Assert.Equal(ErrorIdNotFound, present.Bus.In(0xC9));
            Assert.Equal(missing.Bus.In(0xC9), present.Bus.In(0xC9));
            Assert.Equal(missing.Bus.In(StatusCommandPort), present.Bus.In(StatusCommandPort));

            present.Command(0x30);
            missing.Command(0x30);
            Assert.Equal(ErrorIdNotFound, present.Bus.In(0xC9));
            Assert.Equal(missing.Bus.In(StatusCommandPort), present.Bus.In(StatusCommandPort));
        }

        present.Bus.Out(0xCC, 0x04);
        present.Bus.Out(0xCE, 0x08);
        present.Command(0x10);
        Assert.Equal(0, present.Bus.In(0xC9));
        Assert.Equal(0, present.Bus.In(0xCC));
        Assert.Equal(ReadyAndSeek, present.Bus.In(StatusCommandPort));
    }

    [Fact]
    public void ReadOnlySetsDrive0ProtectAndAbortsTheWrite()
    {
        var board = new Board(Image(1), readOnly: true);
        byte[] original = SectorBytes(0);
        Assert.Equal((byte)(ProtectDrive0 | ProtectAny), board.Bus.In(BasePort));
        Assert.Equal(0, board.Bus.In(BasePort) & 0x01);
        board.Bus.Out(BasePort, 0xFF);
        Assert.Equal((byte)(ProtectDrive0 | ProtectAny), board.Bus.In(BasePort));

        board.Select(sector: 200);
        board.Command(0x30);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC9));
        Assert.Equal(0, board.Bus.In(StatusCommandPort) & StatusDataRequest);

        board.Select();
        board.Command(0x20);
        Assert.Equal(original, board.ReadData());
        board.Command(0x40);
        Assert.Equal(0, board.Bus.In(0xC9));

        byte[] headerProtected = Image(1);
        headerProtected[7] = 0x80;
        var fromHeader = new Board(headerProtected);
        Assert.Equal((byte)(ProtectDrive0 | ProtectAny), fromHeader.Bus.In(BasePort));
        fromHeader.Select();
        fromHeader.Command(0x30);
        Assert.Equal(ErrorAborted, fromHeader.Bus.In(0xC9));
    }

    [Fact]
    public void RestoreClearsTheCylinderAndSeekSetsSeekComplete()
    {
        var board = new Board();
        board.Bus.Out(0xCC, 0x05);
        board.Bus.Out(0xCD, 0x05);
        Assert.Equal(0x01, board.Bus.In(0xCD));
        board.Command(0x7A);
        Assert.Equal(0x05, board.Bus.In(0xCC));
        Assert.Equal(0x01, board.Bus.In(0xCD));
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
        Assert.Equal(0, board.Bus.In(0xC9));

        board.Command(0x1F);
        Assert.Equal(0, board.Bus.In(0xCC));
        Assert.Equal(0, board.Bus.In(0xCD));
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
    }

    [Fact]
    public void VerifySucceedsOnlyWhenTheSectorExists()
    {
        var board = new Board(Image(1));
        board.Select(sector: 0);
        board.Command(0x40);
        Assert.Equal(0, board.Bus.In(0xC9));
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));

        board.Select(sector: 1);
        board.Command(0x40);
        Assert.Equal(ErrorIdNotFound, board.Bus.In(0xC9));

        board.Bus.Out(0xCE, 0x01);
        board.Bus.Out(0xCB, 0);
        board.Command(0x40);
        Assert.Equal(ErrorIdNotFound, board.Bus.In(0xC9));
    }

    [Fact]
    public void FormatAndInitSucceedWithoutWriting()
    {
        var board = new Board(Image(1));
        byte[] original = SectorBytes(0);
        board.Select();
        board.Command(0x00);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC9));

        board.Command(0x50);
        Assert.Equal(0, board.Bus.In(0xC9));
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
        board.Command(0x20);
        Assert.Equal(original, board.ReadData());

        board.Command(0x80);
        board.Command(0x60);
        Assert.Equal(0, board.Bus.In(0xC9));
        board.Command(0x20);
        Assert.Equal(original, board.ReadData());
    }

    [Fact]
    public void TestClearsTheErrorAndLeavesReady()
    {
        var board = new Board();
        board.Command(0x20);
        Assert.Equal(ErrorIdNotFound, board.Bus.In(0xC9));
        board.Command(0x90);
        Assert.Equal(0, board.Bus.In(0xC9));
        Assert.Equal(StatusReady, board.Bus.In(StatusCommandPort));
        Assert.Equal(0, board.Bus.In(StatusCommandPort) & (StatusBusy | StatusError | StatusWriteFault | StatusCorrected));
    }

    [Fact]
    public void UnknownHighNibbleAborts()
    {
        var board = new Board(Image(1));
        foreach (byte command in new byte[] { 0x00, 0x80, 0xA0, 0xF5 })
        {
            board.Command(command);
            Assert.Equal(ErrorAborted, board.Bus.In(0xC9));
            Assert.Equal((byte)(StatusReady | StatusError), board.Bus.In(StatusCommandPort));
        }
    }

    [Fact]
    public void SdhWriteSetsReadyAndSeekCompleteAndCylinderHighKeepsTwoBits()
    {
        var board = new Board();
        Assert.Equal(StatusReady, board.Bus.In(StatusCommandPort));
        board.Bus.Out(0xCD, 0xFF);
        board.Bus.Out(0xCE, 0x80 | 0x40 | 0x18 | 0x05);
        Assert.Equal(0x03, board.Bus.In(0xCD));
        Assert.Equal((byte)(0x40 | 0x18 | 0x05), board.Bus.In(0xCE));
        Assert.Equal(ReadyAndSeek, board.Bus.In(StatusCommandPort));
    }

    [Fact]
    public void ControlByteReadsBackAndBit4ResetsTheTaskFile()
    {
        var board = new Board(Image(1));
        board.Bus.Out(0xC1, 0x0C);
        Assert.Equal(0x0C, board.Bus.In(0xC1));
        board.Bus.Out(0xC2, 0x55);
        board.Bus.Out(0xC3, 0x02);
        board.Bus.Out(0xC5, 0x42);
        board.Bus.Out(0xC6, 0x11);
        board.Bus.Out(0xC7, 0x22);
        board.Bus.Out(0xC9, 0xAA);
        board.Select(sector: 7, cylinder: 9, sdh: 0);
        board.Command(0x00);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC9));
        board.Bus.Out(ExtraCommandPort, 0x05);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC5));
        Assert.Equal(0xFF, board.Bus.In(ExtraCommandPort));
        Assert.Equal(0xFF, board.Bus.In(0xC6));
        Assert.Equal(0xFF, board.Bus.In(0xC7));
        board.Bus.Out(ExtraCommandPort, 0x00);
        Assert.Equal(ErrorAborted, board.Bus.In(0xC5));

        board.Bus.Out(0xC1, 0x1C);
        Assert.Equal(0x1C, board.Bus.In(0xC1));
        Assert.Equal(0, board.Bus.In(0xCB));
        Assert.Equal(0, board.Bus.In(0xCC));
        Assert.Equal(0, board.Bus.In(0xCE));
        Assert.Equal(0, board.Bus.In(0xC9));
        Assert.Equal(0, board.Bus.In(0xCA));
        Assert.Equal(StatusReady, board.Bus.In(StatusCommandPort));
        Assert.Equal(0x55, board.Bus.In(0xC2));
        Assert.Equal(0x02, board.Bus.In(0xC3));
        Assert.Equal(ErrorAborted, board.Bus.In(0xC5));

        board.Controller.Reset();
        Assert.Equal(0, board.Bus.In(0xC1));
        Assert.Equal(0, board.Bus.In(0xC2));
        Assert.Equal(StatusReady, board.Bus.In(StatusCommandPort));
        board.Select();
        board.Command(0x20);
        Assert.Equal(SectorBytes(0), board.ReadData());
    }

    private static byte[] Image(int sectorsPerTrack)
    {
        var file = new byte[256 + (sectorsPerTrack * HardDiskGeometry.BytesPerSector)];
        file[0] = 0x56;
        file[1] = 0xCB;
        file[2] = 0x10;
        file[26] = 1;
        file[28] = 1;
        file[29] = (byte)sectorsPerTrack;
        SectorBytes(0).CopyTo(file, 256);
        return file;
    }

    private static FixedClock Clock() => new(new HardDiskTime(0, 0, 0, 0, 1, 1));

    private static byte[] ReadData(IoBus bus)
    {
        var data = new byte[HardDiskGeometry.BytesPerSector];
        for (int i = 0; i < data.Length; i++)
            data[i] = bus.In(DataPort);

        return data;
    }

    private static byte[] SectorBytes(int sector)
    {
        var data = new byte[HardDiskGeometry.BytesPerSector];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(0x40 + sector + i);

        return data;
    }

    private sealed class Board
    {
        public Board(byte[]? image = null, long delayCycles = 0, bool readOnly = false, IClock? clock = null)
        {
            Slot = new HardDiskSlot();
            if (image is not null)
                Slot.Insert(image, "disk.hdv", writeBack: null, readOnly);

            Controller = new Cp500HardDiskController(
                Slot,
                clock ?? new FixedClock(new HardDiskTime(0, 0, 0, 0, 1, 1)),
                new HardDiskTiming(delayCycles));
            Bus = new IoBus();
            Controller.Register(Bus);
        }

        public HardDiskSlot Slot { get; }

        public Cp500HardDiskController Controller { get; }

        public IoBus Bus { get; }

        public void Select(byte sector = 0, byte cylinder = 0, byte cylinderHigh = 0, byte sdh = 0)
        {
            Bus.Out(0xCB, sector);
            Bus.Out(0xCC, cylinder);
            Bus.Out(0xCD, cylinderHigh);
            Bus.Out(0xCE, sdh);
        }

        public void Command(byte command) => Bus.Out(StatusCommandPort, command);

        public byte[] ReadData()
        {
            var data = new byte[HardDiskGeometry.BytesPerSector];
            for (int i = 0; i < data.Length; i++)
                data[i] = Bus.In(DataPort);

            return data;
        }

        public void WriteData(byte[] data)
        {
            foreach (byte value in data)
                Bus.Out(DataPort, value);
        }
    }

    private sealed class FixedClock(HardDiskTime time) : IClock
    {
        public HardDiskTime Current { get; private set; } = time;

        public HardDiskTime Read() => Current;

        public void Set(HardDiskTime time) => Current = time;
    }
}
