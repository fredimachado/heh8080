using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class MappedMemoryTests
{
    [Fact]
    public void RomOverlaysRamAndRejectsWrites()
    {
        var rom = new RomMemoryRegion(0x0000, new byte[] { 0xC3, 0x15 });
        var ram = new RamMemoryRegion(0x0000, 0x20);
        var memory = new MappedMemory(rom, ram, new UnmappedMemoryRegion(0x0020, 0xFFE0));

        memory.Write(0x0000, 0x00);
        memory.Write(0x0002, 0x42);

        Assert.Equal(0xC3, memory.Read(0x0000));
        Assert.Equal(0x42, memory.Read(0x0002));

        rom.Enabled = false;
        Assert.Equal(0x00, memory.Read(0x0000));
    }

    [Fact]
    public void VideoRamRaisesOnlyForEffectiveChanges()
    {
        var video = new VideoRamRegion(0x3C00, 4);
        var changes = new List<(ushort Address, byte Value)>();
        video.Changed += (address, value) => changes.Add((address, value));

        video.Write(0x3C00, (byte)'A');
        video.Write(0x3C00, (byte)'A');
        video.Write(0x3C01, (byte)'B');

        Assert.Equal(
            new[] { (Address: (ushort)0x3C00, Value: (byte)'A'), (Address: (ushort)0x3C01, Value: (byte)'B') },
            changes);
    }

    [Fact]
    public void KeyboardMatrixIsActiveHighAndOrsSelectedRows()
    {
        var matrix = new KeyboardMatrix();
        var keyboard = new KeyboardMatrixRegion(0x3800, 0x100, matrix);

        Assert.Equal(0x00, keyboard.Read(0x38FF));

        matrix.SetKeyState(0, 0, pressed: true);
        matrix.SetKeyState(1, 1, pressed: true);

        Assert.Equal(0x01, keyboard.Read(0x3801));
        Assert.Equal(0x02, keyboard.Read(0x3802));
        Assert.Equal(0x03, keyboard.Read(0x3803));
        Assert.Equal(0x00, keyboard.Read(0x3804));
        Assert.Equal(0x00, keyboard.Read(0x3800));
    }

    [Fact]
    public void UnmappedMemoryReadsOpenBusAndDiscardsWrites()
    {
        var unmapped = new UnmappedMemoryRegion(0x3000, 0x100, 0xA5);

        unmapped.Write(0x3000, 0x00);

        Assert.Equal(0xA5, unmapped.Read(0x3000));
    }
}

public sealed class Cp500MachineTests
{
    [Fact]
    public void RealRomBootsToBasicAndRunsAStatement()
    {
        var session = new RealRomSession();

        session.Run(seconds: 3);
        string boot = session.Screen();

        Assert.Contains("PROLOGICA CP-500", boot);
        Assert.Contains("BASIC (S ou N)", boot);
        Assert.DoesNotContain("Cass?", boot);

        session.Type("S\r\r");
        session.Run(seconds: 3);
        session.Type("PRINT 22\r");
        session.Run(seconds: 1);
        string basic = session.Screen();

        Assert.Contains("READY", basic);
        Assert.Contains("\n 22", "\n" + basic);
    }

    private sealed class RealRomSession
    {
        private readonly long _tickPeriodCycles;
        private long _cyclesUntilTick;

        public RealRomSession()
        {
            byte[] rom = File.ReadAllBytes(Path.Combine("..", "..", "..", "..", "..", "cp500", "cp500.rom"));
            Machine = new Cp500Machine(Cp500MachineProfile.Original, rom);
            _tickPeriodCycles = Machine.Profile.ClockHz / Machine.Interrupts.Hertz;
            _cyclesUntilTick = _tickPeriodCycles;
        }

        public Cp500Machine Machine { get; }

        public void Run(double seconds)
        {
            long targetCycles = (long)(seconds * Machine.Profile.ClockHz);
            for (long cycles = 0; cycles < targetCycles;)
            {
                int instructionCycles = Machine.Step();
                cycles += instructionCycles;
                _cyclesUntilTick -= instructionCycles;
                if (_cyclesUntilTick <= 0)
                {
                    Machine.TickInterrupt();
                    _cyclesUntilTick += _tickPeriodCycles;
                }
            }
        }

        public void Type(string keys)
        {
            foreach (char key in keys)
            {
                (int row, int column) = MatrixPosition(key);
                Machine.Keyboard.SetKeyState(row, column, pressed: true);
                Run(seconds: 0.1);
                Machine.Keyboard.SetKeyState(row, column, pressed: false);
                Run(seconds: 0.5);
            }
        }

        public string Screen()
        {
            var rows = new List<string>();
            for (int row = 0; row < Cp500VideoDevice.Rows; row++)
            {
                var text = new char[Cp500VideoDevice.Columns];
                for (int column = 0; column < text.Length; column++)
                {
                    byte value = Machine.Video.ReadCharacter(column, row);
                    text[column] = value is >= 0x20 and < 0x7F ? (char)value : ' ';
                }

                rows.Add(new string(text).TrimEnd());
            }

            return string.Join('\n', rows);
        }

        private static (int Row, int Column) MatrixPosition(char key) => key switch
        {
            '\r' => (6, 0),
            ' ' => (6, 7),
            >= '0' and <= '7' => (4, key - '0'),
            '8' => (5, 0),
            '9' => (5, 1),
            >= 'A' and <= 'G' => (0, key - 'A' + 1),
            >= 'H' and <= 'O' => (1, key - 'H'),
            >= 'P' and <= 'W' => (2, key - 'P'),
            >= 'X' and <= 'Z' => (3, key - 'X'),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No CP-500 matrix position.")
        };
    }

    [Fact]
    public void OriginalProfileMapsDevicesRomRamAndOpenBus()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        rom[0] = 0xC3;
        var machine = new Cp500Machine(new Cp500MachineProfile(Cp500Revision.Original, 16), rom);
        var changes = new List<(int Column, int Row, byte Character)>();
        machine.Video.CharacterChanged += (column, row, value) => changes.Add((column, row, value));

        machine.Memory.Write(0x0000, 0x00);
        machine.Memory.Write(0x4000, 0x42);
        machine.Memory.Write(0x3C41, (byte)'X');
        machine.Keyboard.SetKeyState(0, 2, pressed: true);

        Assert.Equal(0xC3, machine.Memory.Read(0x0000));
        Assert.Equal(0x42, machine.Memory.Read(0x4000));
        Assert.Equal(0xFF, machine.Memory.Read(0x8000));
        Assert.Equal(0x04, machine.Memory.Read(0x3801));
        Assert.Equal(new[] { (Column: 1, Row: 1, Character: (byte)'X') }, changes);
        Assert.Equal(1, machine.Cpu.InterruptMode);
    }

    [Fact]
    public void M80ProfileCanExposeUnderlyingLowRam()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        rom[0] = 0xC3;
        var machine = new Cp500Machine(Cp500MachineProfile.M80, rom);

        machine.SetRomOverlay(false);
        machine.Memory.Write(0x0000, 0x5A);
        machine.SetRomOverlay(true);

        Assert.Equal(0xC3, machine.Memory.Read(0x0000));

        machine.SetRomOverlay(false);

        Assert.Equal(0x5A, machine.Memory.Read(0x0000));
    }

    [Fact]
    public void PartialM80RamDoesNotClaimAnAbsentLowRamOverlay()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        rom[0] = 0xC3;
        var profile = new Cp500MachineProfile(Cp500Revision.M80, 48);
        var machine = new Cp500Machine(profile, rom);

        Assert.False(profile.SupportsRomOverlay);
        Assert.Throws<InvalidOperationException>(() => machine.SetRomOverlay(false));
        Assert.Equal(0xC3, machine.Memory.Read(0x0000));
    }

    [Fact]
    public void RtcExposesBcdTimeAndAcceptsBcdWrites()
    {
        var rtc = new Cp500RtcDevice();
        rtc.SetTime(new DateTimeOffset(2026, 8, 19, 12, 34, 56, TimeSpan.Zero));

        Assert.Equal(0x56, rtc.Read(0x4040));
        Assert.Equal(0x34, rtc.Read(0x4041));
        Assert.Equal(0x12, rtc.Read(0x4042));

        rtc.Write(0x4040, 0x05);

        Assert.Equal(0x05, rtc.Read(0x4040));
    }

    [Fact]
    public void Rs232StubReportsOutputReadyAndRetainsControlRegisters()
    {
        var serial = new Cp500Rs232Device();
        var bus = new IoBus();
        serial.Register(bus);

        bus.Out(0xEA, 0x55);
        bus.Out(0xEB, 0xAA);

        Assert.Equal(0x02, bus.In(0xE9));
        Assert.Equal(0x55, bus.In(0xEA));
        Assert.Equal(0xAA, bus.In(0xEB));
    }
}

public sealed class Cp500PortMapTests
{
    private static Cp500Machine CreateMachine(byte[]? rom = null, IModelIIIDiskImageProvider? images = null) =>
        new(Cp500MachineProfile.Original, rom ?? new byte[Cp500MachineProfile.SystemRomSizeBytes], images);

    [Fact]
    public void E0ReportsTimerIrqActiveLowUntilEcAcknowledgesIt()
    {
        var machine = CreateMachine();
        machine.IoBus.Out(0xE0, 0x04);

        machine.TickInterrupt();

        Assert.Equal(0xFB, machine.IoBus.In(0xE0));
        Assert.Equal(0xFB, machine.IoBus.In(0xE3));
        Assert.Equal(0x00, machine.IoBus.In(0xEC));
        Assert.Equal(0xFF, machine.IoBus.In(0xE0));
    }

    [Fact]
    public void MaskedTimerIrqReadsInactive()
    {
        var machine = CreateMachine();
        machine.IoBus.Out(0xE0, 0x00);

        machine.TickInterrupt();

        Assert.Equal(0xFF, machine.IoBus.In(0xE0));
    }

    [Fact]
    public void FfIsACassetteStubThatLeavesInterruptsAlone()
    {
        var machine = CreateMachine();
        machine.IoBus.Out(0xE0, 0x04);
        machine.TickInterrupt();

        machine.IoBus.Out(0xFF, 0x00);

        Assert.Equal(0x00, machine.IoBus.In(0xFF));
        Assert.Equal(0xFB, machine.IoBus.In(0xE0));
    }

    [Fact]
    public void F0ThroughF3AreTheWd1791Registers()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var machine = CreateMachine(images: images);
        machine.IoBus.Out(0xF4, 0x01);

        machine.IoBus.Out(0xF1, 2);
        machine.IoBus.Out(0xF2, 1);
        machine.IoBus.Out(0xF0, 0x80);

        Assert.Equal(2, machine.IoBus.In(0xF1));
        Assert.Equal(1, machine.IoBus.In(0xF2));
        Assert.Equal(0x03, machine.IoBus.In(0xF0));
    }

    [Fact]
    public void E4MasksFdcIntrqIntoActiveLowNmiStatus()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var machine = CreateMachine(images: images);
        machine.IoBus.Out(0xF4, 0x01);

        machine.IoBus.Out(0xE4, 0x80);
        machine.IoBus.Out(0xF0, 0x00);

        Assert.Equal(0x7F, machine.IoBus.In(0xE4));
        Assert.Equal(0xFF, machine.IoBus.In(0xE7));
    }

    [Fact]
    public void F4SelectsTheDriveAndE4DoesNot()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(1, ModelIIIDiskFormat.DoubleSided);
        var machine = CreateMachine(images: images);

        machine.IoBus.Out(0xE4, 0x12);

        Assert.False(machine.DiskController.DriveSelected);

        machine.IoBus.Out(0xF4, 0x12);

        Assert.True(machine.DiskController.DriveSelected);
        Assert.Equal(1, machine.DiskController.SelectedDrive);
        Assert.Equal(1, machine.DiskController.SelectedSide);
    }

    [Fact]
    public void F4ReadsToggleRomAddressLineA11()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        rom[0x0000] = 0x10;
        rom[0x0800] = 0x18;
        rom[0x3000] = 0x30;
        rom[0x3800] = 0x38;
        var machine = CreateMachine(rom);

        Assert.Equal(0x30, machine.Memory.Read(0x3000));
        Assert.Equal(0x00, machine.IoBus.In(0xF4));
        Assert.Equal(0x38, machine.Memory.Read(0x3000));
        Assert.Equal(0x18, machine.Memory.Read(0x0000));
        Assert.Equal(0x18, machine.Memory.Read(0x0800));
        Assert.Equal(0x00, machine.Memory.Read(0x3800));

        Assert.Equal(0x00, machine.IoBus.In(0xF7));
        Assert.Equal(0x30, machine.Memory.Read(0x3000));
        Assert.Equal(0x10, machine.Memory.Read(0x0000));
    }

    [Fact]
    public void ResetRestoresTheUnforcedRomView()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        rom[0x3000] = 0x30;
        rom[0x3800] = 0x38;
        var machine = CreateMachine(rom);
        machine.IoBus.In(0xF4);

        machine.Reset();

        Assert.Equal(0x30, machine.Memory.Read(0x3000));
    }
}

public sealed class Wd1791ControllerTests
{

    [Fact]
    public void DiskControlMirrorsF4ThroughF7AndSurfacesHandshakeState()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(3, ModelIIIDiskFormat.DoubleSided);
        var controller = new Wd1791Controller(images);
        var control = new Cp500DiskControlDevice(controller);
        var bus = new IoBus();
        controller.Register(bus);
        control.Register(bus);
        var drqChanges = new List<bool>();
        var intrqChanges = new List<bool>();
        control.DataRequestChanged += drqChanges.Add;
        control.InterruptRequestChanged += intrqChanges.Add;

        bus.Out(0xF7, 0x58);

        Assert.Equal(3, controller.SelectedDrive);
        Assert.Equal(1, controller.SelectedSide);
        Assert.True(control.WaitRequested);
        Assert.False(control.WaitActive);

        bus.Out(0xF2, 1);
        bus.Out(0xF0, 0x80);

        Assert.True(control.DataRequest);
        Assert.False(control.WaitActive);
        Assert.Equal(new[] { true }, drqChanges);

        for (int index = 0; index < ModelIIIDiskGeometry.BytesPerSector; index++)
            bus.In(0xF3);

        Assert.True(control.InterruptRequest);
        Assert.Contains(true, intrqChanges);
    }

    [Fact]
    public void DefaultImagesAreSingleSidedAndDoubleSidedImagesAreExplicit()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        byte[] sector = Enumerable.Repeat((byte)0x5A, ModelIIIDiskGeometry.BytesPerSector).ToArray();
        byte[] read = new byte[ModelIIIDiskGeometry.BytesPerSector];

        images.MountBlank(0);
        Assert.True(images.WriteSector(0, 39, 0, 18, sector));
        Assert.False(images.ReadSector(0, 0, 1, 1, read));

        images.MountBlank(0, ModelIIIDiskFormat.DoubleSided);
        Assert.True(images.WriteSector(0, 0, 1, 1, sector));
        Assert.True(images.ReadSector(0, 0, 1, 1, read));
        Assert.Equal(sector, read);
        Assert.Equal(180 * 1024, ModelIIIDiskGeometry.DiskSizeBytes);
        Assert.Equal(360 * 1024, ModelIIIDiskGeometry.DoubleSidedDiskSizeBytes);
    }

    [Fact]
    public void StepOnlyUpdatesTrackRegisterWhenUpdateBitIsSet()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        var controller = new Wd1791Controller(images);

        controller.Out(Wd1791Controller.TrackPort, 10);
        controller.Out(Wd1791Controller.CommandStatusPort, 0x40);

        Assert.Equal(10, controller.Track);
        Assert.Equal(1, controller.PhysicalTrack);

        controller.Out(Wd1791Controller.CommandStatusPort, 0x50);

        Assert.Equal(2, controller.Track);
        Assert.Equal(2, controller.PhysicalTrack);

        controller.Out(Wd1791Controller.DataPort, 7);
        controller.Out(Wd1791Controller.CommandStatusPort, 0x10);
        Assert.Equal(7, controller.Track);
        Assert.Equal(7, controller.PhysicalTrack);

        controller.Out(Wd1791Controller.CommandStatusPort, 0x60);
        Assert.Equal(7, controller.Track);
        Assert.Equal(6, controller.PhysicalTrack);

        controller.Out(Wd1791Controller.CommandStatusPort, 0x00);
        Assert.Equal(0, controller.Track);
        Assert.Equal(0, controller.PhysicalTrack);
    }

    [Fact]
    public void MachineResetResetsWd1791AndDiskControlLatches()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(1);
        var machine = new Cp500Machine(Cp500MachineProfile.Original, rom, images);

        machine.IoBus.Out(0xF4, 0x52);
        machine.IoBus.Out(0xF1, 12);
        machine.IoBus.Out(0xF2, 8);
        machine.IoBus.Out(0xF3, 0xA5);
        machine.IoBus.Out(0xF0, 0x80);
        machine.Reset();

        Assert.Equal(0, machine.DiskController.Track);
        Assert.Equal(0, machine.DiskController.PhysicalTrack);
        Assert.Equal(1, machine.DiskController.Sector);
        Assert.Equal(0, machine.DiskController.Data);
        Assert.False(machine.DiskController.DataRequest);
        Assert.False(machine.DiskController.InterruptRequest);
        Assert.False(machine.DiskController.DriveSelected);
        Assert.Equal(0, machine.DiskControl.ControlLatch);
        Assert.False(machine.DiskControl.WaitRequested);
        Assert.False(machine.DiskControl.WaitActive);
        Assert.True(images.IsMounted(1));
    }

    [Fact]
    public void WriteThenReadSectorUsesModelThreeGeometry()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var controller = new Wd1791Controller(images);
        var bus = new IoBus();
        controller.Register(bus);

        bus.Out(Wd1791Controller.TrackPort, 0);
        bus.Out(Wd1791Controller.SectorPort, 1);
        bus.Out(Wd1791Controller.CommandStatusPort, 0xA0);
        Assert.True(controller.DataRequest);

        for (int value = 0; value < ModelIIIDiskGeometry.BytesPerSector; value++)
            bus.Out(Wd1791Controller.DataPort, (byte)value);

        Assert.False(controller.DataRequest);
        Assert.True(controller.InterruptRequest);

        bus.Out(Wd1791Controller.CommandStatusPort, 0x80);
        byte[] sector = new byte[ModelIIIDiskGeometry.BytesPerSector];
        for (int index = 0; index < sector.Length; index++)
            sector[index] = bus.In(Wd1791Controller.DataPort);

        Assert.Equal(Enumerable.Range(0, ModelIIIDiskGeometry.BytesPerSector).Select(value => (byte)value), sector);
        Assert.False(controller.DataRequest);
        Assert.True(controller.InterruptRequest);
    }

    [Fact]
    public void InvalidSectorCompletesWithRecordNotFound()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var controller = new Wd1791Controller(images);

        controller.Out(Wd1791Controller.TrackPort, 0);
        controller.Out(Wd1791Controller.SectorPort, 19);
        controller.Out(Wd1791Controller.CommandStatusPort, 0x80);

        Assert.False(controller.DataRequest);
        Assert.True(controller.InterruptRequest);
        Assert.True((controller.Status & 0x10) != 0);
    }

    [Fact]
    public void InterruptFabricUsesActiveLowMasksAndZ80NmiVector()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        var machine = new Cp500Machine(Cp500MachineProfile.M80, rom);
        machine.SetRomOverlay(false);
        machine.Cpu.PC = 0x1234;
        machine.Cpu.SP = 0x8000;
        machine.Interrupts.WriteNmiMask(Cp500InterruptDevice.NmiReset);

        machine.Interrupts.RequestResetNmi();
        byte status = machine.Interrupts.ReadNmiStatus();

        Assert.Equal(0x0066, machine.Cpu.PC);
        Assert.Equal(0x7FFE, machine.Cpu.SP);
        Assert.Equal(0x34, machine.Memory.Read(0x7FFE));
        Assert.Equal(0x12, machine.Memory.Read(0x7FFF));
        Assert.Equal(0xDF, status);
    }

    [Fact]
    public void TimedWd1791DelaysDrqAndReportsLostData()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var controller = new Wd1791Controller(images, new Wd1791Timing(CommandDelayCycles: 10, ByteWindowCycles: 5));

        controller.Out(Wd1791Controller.SectorPort, 1);
        controller.Out(Wd1791Controller.CommandStatusPort, 0x80);

        Assert.True((controller.Status & Wd1791Controller.StatusBusy) != 0);
        Assert.False(controller.DataRequest);

        controller.AdvanceCycles(10);

        Assert.True(controller.DataRequest);

        controller.AdvanceCycles(5);

        Assert.False(controller.DataRequest);
        Assert.True(controller.InterruptRequest);
        Assert.True((controller.Status & Wd1791Controller.StatusLostData) != 0);
    }

    [Fact]
    public void Wd1791SupportsReadAddressReadTrackAndWriteTrack()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var controller = new Wd1791Controller(images);

        controller.Out(Wd1791Controller.SectorPort, 1);
        controller.Out(Wd1791Controller.CommandStatusPort, 0xC0);
        byte[] address = Enumerable.Range(0, 6).Select(_ => controller.In(Wd1791Controller.DataPort)).ToArray();

        Assert.Equal(new byte[] { 0, 0, 1, 1, 0, 0 }, address);

        controller.Out(Wd1791Controller.CommandStatusPort, 0xF0);
        for (int index = 0; index < ModelIIIDiskGeometry.SectorsPerTrack * ModelIIIDiskGeometry.BytesPerSector; index++)
            controller.Out(Wd1791Controller.DataPort, (byte)(index & 0xFF));

        controller.Out(Wd1791Controller.CommandStatusPort, 0xE0);

        Assert.Equal(0, controller.In(Wd1791Controller.DataPort));
        Assert.Equal(1, controller.In(Wd1791Controller.DataPort));
    }

    [Fact]
    public void DiskCodecsRoundTripRawJv3AndDmkWithMetadata()
    {
        var image = ModelIIIDiskImage.CreateBlank(ModelIIIDiskFormat.SingleSided);
        image.FindSector(0, 0, 1)!.Data[0] = 0xA5;

        foreach (var kind in new[] { ModelIIIDiskImageKind.Raw, ModelIIIDiskImageKind.Jv3, ModelIIIDiskImageKind.Dmk })
        {
            byte[] serialized = ModelIIIDiskCodec.Serialize(image, kind);
            var parsed = ModelIIIDiskCodec.Parse(serialized, kind);

            Assert.Equal(0xA5, parsed.FindSector(0, 0, 1)!.Data[0]);
            Assert.True(parsed.TryGetFirstHeader(0, 0, out var header));
            Assert.Equal(1, header.Id);
        }

        Assert.Throws<InvalidDataException>(() => ModelIIIDiskCodec.Parse(new byte[123], ModelIIIDiskImageKind.Raw));
    }

    [Fact]
    public void VideoControlSelects32ColumnAndBit7FallbackWithoutMutatingVram()
    {
        var video = new Cp500VideoDevice();
        video.WriteCharacter(2, 0, 0xC1);
        byte before = video.ReadCharacter(2, 0);

        video.Out(Cp500VideoDevice.ControlPort, 0x04);
        var normal = video.GetRenderedCell(1, 0);
        video.Out(Cp500VideoDevice.ControlPort, 0x0C);
        var alternate = video.GetRenderedCell(1, 0);

        Assert.Equal(32, video.VisibleColumns);
        Assert.Equal(before, video.ReadCharacter(2, 0));
        Assert.True(normal.Inverse);
        Assert.False(normal.Graphics);
        Assert.True(alternate.Graphics);
        Assert.False(alternate.Inverse);
    }
}
