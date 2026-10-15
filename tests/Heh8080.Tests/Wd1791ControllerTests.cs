using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed partial class Wd1791ControllerTests
{
    private const byte Command = Wd1791Controller.CommandStatusPort;
    private const byte TrackPort = Wd1791Controller.TrackPort;
    private const byte SectorPort = Wd1791Controller.SectorPort;
    private const byte DataPort = Wd1791Controller.DataPort;
    private const byte StatusBusy = Wd1791Controller.StatusBusy;
    private const byte StatusTrackZero = Wd1791Controller.StatusTrackZero;
    private const byte StatusRecordNotFound = Wd1791Controller.StatusRecordNotFound;
    private const byte StatusRecordType = Wd1791Controller.StatusRecordType;
    private const byte StatusCrcError = Wd1791Controller.StatusCrcError;
    private const byte StatusIndex = Wd1791Controller.StatusIndex;
    private const byte StatusNotReady = Wd1791Controller.StatusNotReady;

    private static (Wd1791Controller Controller, MemoryModelIIIDiskImageProvider Images) Create(
        ModelIIIDiskFormat format = ModelIIIDiskFormat.SingleSided,
        bool readOnly = false)
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0, format, readOnly);
        return (new Wd1791Controller(images), images);
    }

    private static byte[] Drain(Wd1791Controller controller)
    {
        var bytes = new List<byte>();
        while (controller.DataRequest)
            bytes.Add(controller.In(DataPort));

        return [.. bytes];
    }

    private static void Feed(Wd1791Controller controller, Func<int, byte> value)
    {
        for (int index = 0; controller.DataRequest; index++)
            controller.Out(DataPort, value(index));
    }

    private static void SeekTo(Wd1791Controller controller, byte track)
    {
        controller.Out(DataPort, track);
        controller.Out(Command, 0x10);
    }

    [Fact]
    public void ForceInterruptD0TerminatesTheCommandWithoutRaisingIntrq()
    {
        var (controller, _) = Create();
        var intrq = new List<bool>();
        controller.InterruptRequestChanged += intrq.Add;
        controller.Out(Command, 0x80);
        Assert.True(controller.DataRequest);

        controller.Out(Command, 0xD0);

        Assert.False(controller.DataRequest);
        Assert.Equal(0, controller.Status & StatusBusy);
        Assert.False(controller.InterruptRequest);
        Assert.DoesNotContain(true, intrq);
    }

    [Fact]
    public void ForceInterruptD0WhileIdleDoesNotRaiseIntrq()
    {
        var (controller, _) = Create();

        controller.Out(Command, 0xD0);

        Assert.False(controller.InterruptRequest);
    }

    [Fact]
    public void ForceInterruptD8RaisesIntrqImmediately()
    {
        var (controller, _) = Create();

        controller.Out(Command, 0xD8);

        Assert.True(controller.InterruptRequest);
        Assert.Equal(0, controller.Status & StatusBusy);
    }

    [Fact]
    public void ForceInterruptOnIndexRaisesIntrqAtTheNextIndexPulse()
    {
        var (controller, _) = Create();
        controller.Out(Command, 0xD4);
        Assert.False(controller.InterruptRequest);

        controller.AdvanceCycles(Wd1791Timing.DefaultRevolutionCycles);

        Assert.True(controller.InterruptRequest);
    }

    [Fact]
    public void ForceInterruptWhileIdleLeavesTypeIStatus()
    {
        var (controller, _) = Create();
        controller.Out(SectorPort, 19);
        controller.Out(Command, 0x80);
        Assert.Equal(StatusRecordNotFound, controller.Status);

        controller.Out(Command, 0xD0);

        Assert.Equal(StatusTrackZero, controller.Status);
    }

    [Fact]
    public void ForceInterruptD0DoesNotRaiseTheModelIIINmi()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var machine = new Cp500Machine(Cp500MachineProfile.Original, new byte[Cp500MachineProfile.SystemRomSizeBytes], images);
        machine.IoBus.Out(0xF4, 0x81);
        machine.IoBus.Out(0xE4, 0x80);

        machine.IoBus.Out(0xF0, 0xD0);

        Assert.Equal(0xFF, machine.IoBus.In(0xE4));
    }

    [Fact]
    public void TypeIStatusReportsTrackZeroOnlyWhileTheHeadIsAtTrackZero()
    {
        var (controller, _) = Create();

        controller.Out(Command, 0x00);
        Assert.Equal(StatusTrackZero, controller.Status);

        SeekTo(controller, 5);
        Assert.Equal(0, controller.Status & StatusTrackZero);
    }

    [Fact]
    public void TypeIStatusReportsHeadLoadedWriteProtectAndNotReady()
    {
        var (controller, _) = Create(readOnly: true);

        controller.Out(Command, 0x08);
        Assert.Equal(0x20, controller.Status & 0x20);
        Assert.Equal(0x40, controller.Status & 0x40);

        controller.Out(Command, 0x00);
        Assert.Equal(0, controller.Status & 0x20);

        controller.DeselectDrive();
        Assert.Equal(0x80, controller.Status & 0x80);
        Assert.Equal(0x80, new Wd1791Controller(new MemoryModelIIIDiskImageProvider()).Status & 0x80);
    }

    [Fact]
    public void AnInstalledEmptyDriveReportsTrackZeroNotReadyAndNoIndexPulse()
    {
        var empty = new Wd1791Controller(new MemoryModelIIIDiskImageProvider(), installedDrives: 2);
        empty.Out(Command, 0x00);
        int indexPulses = 0;

        for (long cycle = 0; cycle < Wd1791Timing.DefaultRevolutionCycles; cycle += 1000)
        {
            indexPulses += empty.Status & StatusIndex;
            empty.AdvanceCycles(1000);
        }

        Assert.Equal(StatusTrackZero | StatusNotReady, empty.Status);
        Assert.Equal(0, indexPulses);
    }

    [Fact]
    public void ADriveThatIsNotInstalledNeverReportsTrackZeroAndNeverMoves()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(2);
        var controller = new Wd1791Controller(images, installedDrives: 2);
        controller.SelectDrive(2);

        controller.Out(Command, 0x00);
        Assert.Equal(StatusNotReady, controller.Status);

        SeekTo(controller, 5);
        Assert.Equal(0, controller.PhysicalTrack);
        Assert.Equal(0, controller.Status & StatusTrackZero);

        controller.Out(SectorPort, 1);
        controller.Out(Command, 0x80);
        Assert.Equal(StatusNotReady, controller.Status & StatusNotReady);
    }

    [Fact]
    public void TypeIIndexBitPulsesOncePerRevolutionOnlyWithADiskSpinning()
    {
        var (controller, _) = Create();
        var empty = new Wd1791Controller(new MemoryModelIIIDiskImageProvider());
        controller.Out(Command, 0x00);
        empty.Out(Command, 0x00);
        int pulses = 0;
        int emptyPulses = 0;

        for (long cycle = 0; cycle < Wd1791Timing.DefaultRevolutionCycles; cycle += 1000)
        {
            pulses += controller.Status & StatusIndex;
            emptyPulses += empty.Status & StatusIndex;
            controller.AdvanceCycles(1000);
            empty.AdvanceCycles(1000);
        }

        Assert.InRange(pulses / StatusIndex, 1, 20);
        Assert.Equal(0, emptyPulses);
    }

    [Fact]
    public void WriteSectorWithA0WritesADeletedMarkThatReadSectorReportsAsRecordType()
    {
        var (controller, images) = Create();
        controller.Out(SectorPort, 5);

        controller.Out(Command, 0xA1);
        Feed(controller, index => (byte)index);

        Assert.True(images.GetTrack(0, 0, 0).Single(sector => sector.Id == 5).DeletedData);
        controller.Out(Command, 0x80);
        byte[] data = Drain(controller);
        Assert.Equal(Enumerable.Range(0, 256).Select(value => (byte)value), data);
        Assert.Equal(StatusRecordType, controller.Status & StatusRecordType);

        controller.Out(SectorPort, 6);
        controller.Out(Command, 0x80);
        Drain(controller);
        Assert.Equal(0, controller.Status & StatusRecordType);
    }

    [Fact]
    public void WriteSectorWithoutA0ClearsTheDeletedMark()
    {
        var (controller, images) = Create();
        controller.Out(SectorPort, 5);
        controller.Out(Command, 0xA1);
        Feed(controller, _ => 1);

        controller.Out(Command, 0xA0);
        Feed(controller, _ => 2);

        Assert.False(images.GetTrack(0, 0, 0).Single(sector => sector.Id == 5).DeletedData);
    }

    [Fact]
    public void MultiSectorReadContinuesUntilRecordNotFound()
    {
        var (controller, images) = Create();
        for (int id = 1; id <= 18; id++)
            images.WriteSector(0, 0, 0, id, Enumerable.Repeat((byte)id, 256).ToArray());
        controller.Out(SectorPort, 17);

        controller.Out(Command, 0x90);
        byte[] data = Drain(controller);

        Assert.Equal(512, data.Length);
        Assert.Equal(17, data[0]);
        Assert.Equal(18, data[256]);
        Assert.Equal(19, controller.Sector);
        Assert.Equal(StatusRecordNotFound, controller.Status & StatusRecordNotFound);
        Assert.Equal(0, controller.Status & StatusBusy);
        Assert.True(controller.InterruptRequest);
    }

    [Fact]
    public void MultiSectorReadCanBeStoppedByForceInterrupt()
    {
        var (controller, _) = Create();
        controller.Out(Command, 0x90);
        for (int index = 0; index < 256 + 10; index++)
            controller.In(DataPort);

        controller.Out(Command, 0xD0);

        Assert.False(controller.DataRequest);
        Assert.Equal(0, controller.Status & StatusBusy);
        Assert.Equal(2, controller.Sector);
    }

    [Fact]
    public void MultiSectorWriteContinuesUntilRecordNotFound()
    {
        var (controller, images) = Create();
        controller.Out(SectorPort, 17);

        controller.Out(Command, 0xB0);
        Feed(controller, index => (byte)(index < 256 ? 0x17 : 0x18));

        Assert.All(images.GetTrack(0, 0, 0).Single(sector => sector.Id == 17).Data, value => Assert.Equal(0x17, value));
        Assert.All(images.GetTrack(0, 0, 0).Single(sector => sector.Id == 18).Data, value => Assert.Equal(0x18, value));
        Assert.Equal(StatusRecordNotFound, controller.Status & StatusRecordNotFound);
        Assert.Equal(0, controller.Status & StatusBusy);
    }

    [Fact]
    public void CommandsWrittenWhileBusyAreIgnoredExceptForceInterrupt()
    {
        var (controller, _) = Create();
        controller.Out(Command, 0x80);
        Assert.True(controller.DataRequest);

        SeekTo(controller, 9);
        controller.Out(Command, 0x00);

        Assert.True(controller.DataRequest);
        Assert.Equal(0, controller.PhysicalTrack);
        Assert.Equal(256, Drain(controller).Length);
    }

    [Fact]
    public void ReadAddressCyclesThroughIdsWithRealCrcAndCopiesTheTrackIntoTheSectorRegister()
    {
        var (controller, _) = Create();
        SeekTo(controller, 5);

        var ids = new List<byte[]>();
        for (int index = 0; index < 20; index++)
        {
            controller.Out(Command, 0xC0);
            ids.Add(Drain(controller));
        }

        Assert.Equal(Enumerable.Range(1, 18).Concat([1, 2]), ids.Select(id => (int)id[2]));
        Assert.Equal(5, controller.Sector);
        foreach (byte[] id in ids)
        {
            Assert.Equal(new byte[] { 5, 0 }, id[..2]);
            Assert.Equal(1, id[3]);
            Assert.Equal(Crc(0xA1, 0xA1, 0xA1, 0xFE, 5, 0, id[2], 1), (id[4] << 8) | id[5]);
        }
    }

    [Fact]
    public void ReadAddressReturnsTheRecordedTrackAndSideOfSectorsWithMismatchedIds()
    {
        var (controller, images) = Create();
        images.ReplaceTrack(0, 0, 0, [new ModelIIIDiskSector(0, 0, 3, new byte[256], IdTrack: 7)]);

        controller.Out(Command, 0xC0);

        Assert.Equal(new byte[] { 7, 0, 3, 1 }, Drain(controller)[..4]);
        Assert.Equal(7, controller.Sector);
    }

    [Fact]
    public void SeekMovesTheHeadRelativeToTheTrackRegisterAndClampsAtTheEnds()
    {
        var (controller, _) = Create();
        controller.Out(TrackPort, 20);

        SeekTo(controller, 22);

        Assert.Equal(2, controller.PhysicalTrack);
        Assert.Equal(22, controller.Track);

        controller.Out(TrackPort, 30);
        SeekTo(controller, 25);

        Assert.Equal(0, controller.PhysicalTrack);
        Assert.Equal(25, controller.Track);

        controller.Out(TrackPort, 0);
        SeekTo(controller, 200);

        Assert.Equal(ModelIIIDiskGeometry.Tracks - 1, controller.PhysicalTrack);
    }

    [Fact]
    public void RestoreStepsToTrackZeroAndClearsTheTrackRegister()
    {
        var (controller, _) = Create();
        SeekTo(controller, 12);

        controller.Out(Command, 0x00);

        Assert.Equal(0, controller.PhysicalTrack);
        Assert.Equal(0, controller.Track);
    }

    [Fact]
    public void EachDriveKeepsItsOwnHeadPositionWhileTheTrackRegisterIsShared()
    {
        var (controller, images) = Create();
        images.MountBlank(1);
        SeekTo(controller, 10);

        controller.SelectDrive(1);

        Assert.Equal(0, controller.PhysicalTrack);
        Assert.Equal(10, controller.Track);

        controller.Out(Command, 0x40);
        controller.SelectDrive(0);

        Assert.Equal(10, controller.PhysicalTrack);
        controller.SelectDrive(1);
        Assert.Equal(1, controller.PhysicalTrack);
    }

    [Fact]
    public void SectorLookupComparesTheTrackRegisterWithTheIdTrackWhileTheHeadChoosesThePhysicalTrack()
    {
        var (controller, _) = Create();
        SeekTo(controller, 3);

        controller.Out(TrackPort, 4);
        controller.Out(Command, 0x80);

        Assert.False(controller.DataRequest);
        Assert.Equal(StatusRecordNotFound, controller.Status & StatusRecordNotFound);

        controller.Out(TrackPort, 3);
        controller.Out(Command, 0x80);

        Assert.True(controller.DataRequest);
    }

    [Fact]
    public void SectorsWhoseIdTrackDiffersAreFoundByTheirRecordedTrack()
    {
        var (controller, images) = Create();
        images.ReplaceTrack(0, 0, 0, [new ModelIIIDiskSector(0, 0, 1, new byte[256], IdTrack: 7)]);

        controller.Out(Command, 0x80);
        Assert.Equal(StatusRecordNotFound, controller.Status & StatusRecordNotFound);

        controller.Out(TrackPort, 7);
        controller.Out(Command, 0x80);
        Assert.True(controller.DataRequest);
    }

    [Fact]
    public void VerifyFlagReportsSeekErrorWhenTheTargetTrackHasNoMatchingId()
    {
        var (controller, images) = Create();
        images.ReplaceTrack(0, 4, 0, []);

        controller.Out(DataPort, 3);
        controller.Out(Command, 0x14);
        Assert.Equal(0, controller.Status & StatusRecordNotFound);

        controller.Out(DataPort, 4);
        controller.Out(Command, 0x14);
        Assert.Equal(StatusRecordNotFound, controller.Status & StatusRecordNotFound);
    }

    [Fact]
    public void SectorsOfOtherSizesTransferTheirActualLength()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        var image = new ModelIIIDiskImage(
            ModelIIIDiskFormat.SingleSided,
            [
                new ModelIIIDiskSector(0, 0, 1, Enumerable.Repeat((byte)0x51, 512).ToArray()),
                new ModelIIIDiskSector(0, 0, 2, Enumerable.Repeat((byte)0x52, 128).ToArray())
            ]);
        images.Mount(0, image);
        var controller = new Wd1791Controller(images);

        controller.Out(SectorPort, 1);
        controller.Out(Command, 0x80);
        byte[] big = Drain(controller);
        controller.Out(SectorPort, 2);
        controller.Out(Command, 0x80);
        byte[] small = Drain(controller);

        Assert.Equal(Enumerable.Repeat((byte)0x51, 512), big);
        Assert.Equal(Enumerable.Repeat((byte)0x52, 128), small);

        controller.Out(SectorPort, 1);
        controller.Out(Command, 0xA0);
        Feed(controller, _ => 0x61);
        Assert.Equal(Enumerable.Repeat((byte)0x61, 512), images.GetTrack(0, 0, 0)[0].Data);
    }

    [Fact]
    public void SectorWithADataCrcErrorTransfersThenReportsCrcError()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.Mount(0, new ModelIIIDiskImage(
            ModelIIIDiskFormat.SingleSided,
            [new ModelIIIDiskSector(0, 0, 1, new byte[256], CrcValid: false)]));
        var controller = new Wd1791Controller(images);

        controller.Out(SectorPort, 1);
        controller.Out(Command, 0x80);

        Assert.Equal(256, Drain(controller).Length);
        Assert.Equal(StatusCrcError, controller.Status & StatusCrcError);
        Assert.True(controller.InterruptRequest);
    }

    [Fact]
    public void SectorsOfTheOtherDensityAreNotFound()
    {
        var (controller, _) = Create();
        controller.SelectDensity(ModelIIIDiskDensity.SingleDensity);

        controller.Out(SectorPort, 1);
        controller.Out(Command, 0x80);
        Assert.Equal(StatusRecordNotFound, controller.Status & StatusRecordNotFound);

        controller.Out(Command, 0xC0);
        Assert.Equal(StatusRecordNotFound, controller.Status & StatusRecordNotFound);

        controller.SelectDensity(ModelIIIDiskDensity.DoubleDensity);
        controller.Out(Command, 0x80);
        Assert.True(controller.DataRequest);
    }

    [Fact]
    public void ControlLatchBit7SelectsMfmOrFm()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var controller = new Wd1791Controller(images);
        var control = new Cp500DiskControlDevice(controller);
        var bus = new IoBus();
        controller.Register(bus);
        control.Register(bus);
        bus.Out(0xF2, 1);

        bus.Out(0xF4, 0x01);
        bus.Out(0xF0, 0x80);
        Assert.Equal(ModelIIIDiskDensity.SingleDensity, control.SelectedDensity);
        Assert.Equal(StatusRecordNotFound, controller.Status & StatusRecordNotFound);

        bus.Out(0xF4, 0x81);
        bus.Out(0xF0, 0x80);
        Assert.Equal(ModelIIIDiskDensity.DoubleDensity, control.SelectedDensity);
        Assert.True(controller.DataRequest);

        control.Reset();
        Assert.Equal(ModelIIIDiskDensity.SingleDensity, controller.Density);
    }

    [Fact]
    public void WriteTrackAcceptsAFullRawMfmTrackAndRebuildsTheSectors()
    {
        var (controller, images) = Create();
        int[] ids = [1, 10, 2, 11, 3, 12, 4, 13, 5, 14, 6, 15, 7, 16, 8, 17, 9, 18];
        byte[] stream = BuildMfmFormatStream(0, 0, ids, sizeCode: 1, fill: 0xE5);
        Assert.Equal(6250, stream.Length);

        controller.Out(Command, 0xF0);
        for (int index = 0; index < stream.Length - 1; index++)
        {
            controller.Out(DataPort, stream[index]);
            Assert.True(controller.DataRequest);
        }

        controller.Out(DataPort, stream[^1]);

        Assert.False(controller.DataRequest);
        Assert.Equal(0, controller.Status & StatusBusy);
        Assert.True(controller.InterruptRequest);
        var track = images.GetTrack(0, 0, 0);
        Assert.Equal(ids, track.Select(sector => sector.Id));
        Assert.All(track, sector => Assert.Equal(256, sector.Data.Length));
        Assert.All(track, sector => Assert.All(sector.Data, value => Assert.Equal(0xE5, value)));
        Assert.All(track, sector => Assert.Equal(ModelIIIDiskDensity.DoubleDensity, sector.Density));
        Assert.All(track, sector => Assert.True(sector.CrcValid));
    }

    [Fact]
    public void WriteTrackCanFormatDifferentSectorSizesAndIdTracks()
    {
        var (controller, images) = Create();
        byte[] stream = BuildMfmFormatStream(7, 0, [1, 2, 3, 4, 5, 6, 7, 8], sizeCode: 2, fill: 0x6B);

        controller.Out(Command, 0xF0);
        foreach (byte value in stream)
            controller.Out(DataPort, value);

        var track = images.GetTrack(0, 0, 0);
        Assert.Equal(8, track.Count);
        Assert.All(track, sector => Assert.Equal(512, sector.Data.Length));
        Assert.All(track, sector => Assert.Equal(7, sector.RecordedTrack));

        controller.Out(TrackPort, 7);
        controller.Out(SectorPort, 3);
        controller.Out(Command, 0x80);
        Assert.Equal(512, Drain(controller).Length);
    }

    [Fact]
    public void WriteTrackOnAWriteProtectedDiskFailsWithoutTransfer()
    {
        var (controller, _) = Create(readOnly: true);

        controller.Out(Command, 0xF0);

        Assert.False(controller.DataRequest);
        Assert.Equal(0x40, controller.Status & 0x40);
        Assert.True(controller.InterruptRequest);
    }

    [Fact]
    public void ReadTrackReturnsTheRawMfmTrackThatWriteTrackFormatted()
    {
        var (controller, _) = Create();
        int[] ids = [1, 10, 2, 11, 3, 12, 4, 13, 5, 14, 6, 15, 7, 16, 8, 17, 9, 18];
        controller.Out(Command, 0xF0);
        foreach (byte value in BuildMfmFormatStream(0, 0, ids, sizeCode: 1, fill: 0x5A))
            controller.Out(DataPort, value);

        controller.Out(Command, 0xE0);
        byte[] track = Drain(controller);

        Assert.Equal(6250, track.Length);
        Assert.Equal(ids, FindIdAddressMarks(track));
        var payloads = FindDataFields(track, 256);
        Assert.Equal(18, payloads.Count);
        Assert.All(payloads, payload => Assert.All(payload, value => Assert.Equal(0x5A, value)));
        Assert.Equal(0, controller.Status & StatusBusy);
    }

    [Fact]
    public void ReadTrackOfAnFmSelectionUsesTheFmTrackLength()
    {
        var (controller, _) = Create();
        controller.SelectDensity(ModelIIIDiskDensity.SingleDensity);

        controller.Out(Command, 0xE0);

        Assert.Equal(3125, Drain(controller).Length);
    }

    private static List<int> FindIdAddressMarks(byte[] track)
    {
        var ids = new List<int>();
        for (int index = 3; index + 4 < track.Length; index++)
        {
            if (track[index - 3] == 0xA1 && track[index - 2] == 0xA1 && track[index - 1] == 0xA1 && track[index] == 0xFE)
                ids.Add(track[index + 3]);
        }

        return ids;
    }

    private static List<byte[]> FindDataFields(byte[] track, int length)
    {
        var payloads = new List<byte[]>();
        for (int index = 3; index + length < track.Length; index++)
        {
            if (track[index - 3] == 0xA1 && track[index - 2] == 0xA1 && track[index - 1] == 0xA1 && track[index] == 0xFB)
                payloads.Add(track[(index + 1)..(index + 1 + length)]);
        }

        return payloads;
    }

    private static byte[] BuildMfmFormatStream(int cylinder, int side, int[] ids, int sizeCode, byte fill)
    {
        var stream = new List<byte>();
        stream.AddRange(Enumerable.Repeat((byte)0x4E, 32));
        stream.AddRange(Enumerable.Repeat((byte)0x00, 12));
        stream.AddRange(Enumerable.Repeat((byte)0xF6, 3));
        stream.Add(0xFC);
        stream.AddRange(Enumerable.Repeat((byte)0x4E, 32));
        foreach (int id in ids)
        {
            stream.AddRange(Enumerable.Repeat((byte)0x00, 12));
            stream.AddRange(Enumerable.Repeat((byte)0xF5, 3));
            stream.AddRange([0xFE, (byte)cylinder, (byte)side, (byte)id, (byte)sizeCode, 0xF7]);
            stream.AddRange(Enumerable.Repeat((byte)0x4E, 22));
            stream.AddRange(Enumerable.Repeat((byte)0x00, 12));
            stream.AddRange(Enumerable.Repeat((byte)0xF5, 3));
            stream.Add(0xFB);
            stream.AddRange(Enumerable.Repeat(fill, 128 << sizeCode));
            stream.Add(0xF7);
            stream.AddRange(Enumerable.Repeat((byte)0x4E, 24));
        }

        stream.AddRange(Enumerable.Repeat((byte)0x4E, 6250 - stream.Count));
        return [.. stream];
    }

    private static int Crc(params byte[] bytes)
    {
        int crc = 0xFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
        }

        return crc;
    }
}
