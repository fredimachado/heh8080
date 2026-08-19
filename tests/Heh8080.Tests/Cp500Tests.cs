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
    public void KeyboardMatrixReadsSelectedActiveLowRows()
    {
        var matrix = new KeyboardMatrix();
        matrix.SetKeyState(0, 0, pressed: true);
        matrix.SetKeyState(1, 1, pressed: true);
        var keyboard = new KeyboardMatrixRegion(0x3800, 0x100, matrix);

        Assert.Equal(0xFE, keyboard.Read(0x3801));
        Assert.Equal(0xFD, keyboard.Read(0x3802));
        Assert.Equal(0xFF, keyboard.Read(0x3803));
        Assert.Equal(0xFF, keyboard.Read(0x3800));
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
    public void RealRomIdleKeyboardDoesNotInjectACharacter()
    {
        byte[] rom = File.ReadAllBytes(Path.Combine("..", "..", "..", "..", "..", "cp500", "CP500-1.BIN"));
        var machine = new Cp500Machine(Cp500MachineProfile.Original, rom);
        for (int i = 0; i < 5_000_000; i++)
            machine.Step();

        Assert.Equal((byte)'C', machine.Video.ReadCharacter(0, 1));
        Assert.Equal((byte)'a', machine.Video.ReadCharacter(1, 1));
        Assert.Equal((byte)'s', machine.Video.ReadCharacter(2, 1));
        Assert.Equal((byte)'s', machine.Video.ReadCharacter(3, 1));
        Assert.Equal((byte)'?', machine.Video.ReadCharacter(4, 1));
        Assert.Equal((byte)' ', machine.Video.ReadCharacter(5, 1));
    }

    [Fact]
    public void RealRomAcceptsCassetteResponse()
    {
        byte[] rom = File.ReadAllBytes(Path.Combine("..", "..", "..", "..", "..", "cp500", "CP500-1.BIN"));
        var machine = new Cp500Machine(Cp500MachineProfile.Original, rom);
        RunUntilText(machine, "Cass?", 5_000_000);

        PressKey(machine, 6, 0);
        RunForCycles(machine, 100_000);

        Assert.True(ContainsText(machine, "Mem. usada ?"));
    }

    private static bool ContainsText(Cp500Machine machine, string text)
    {
        for (int row = 0; row < Cp500VideoDevice.Rows; row++)
        {
            for (int column = 0; column <= Cp500VideoDevice.Columns - text.Length; column++)
            {
                bool matches = true;
                for (int index = 0; index < text.Length; index++)
                {
                    if (machine.Video.ReadCharacter(column + index, row) != text[index])
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return true;
            }
        }

        return false;
    }

    private static void RunUntilText(Cp500Machine machine, string text, int maximumInstructions)
    {
        int cyclesUntilInterrupt = (int)(machine.Profile.ClockHz / machine.Interrupts.Hertz);
        for (int instruction = 0; instruction < maximumInstructions; instruction++)
        {
            cyclesUntilInterrupt -= machine.Step();
            if (cyclesUntilInterrupt <= 0)
            {
                machine.TickInterrupt();
                cyclesUntilInterrupt += (int)(machine.Profile.ClockHz / machine.Interrupts.Hertz);
            }

            if (ContainsText(machine, text))
                return;
        }

        throw new Xunit.Sdk.XunitException($"ROM did not display '{text}'.");
    }

    private static void RunForCycles(Cp500Machine machine, int targetCycles)
    {
        int cyclesUntilInterrupt = (int)(machine.Profile.ClockHz / machine.Interrupts.Hertz);
        for (int cycles = 0; cycles < targetCycles;)
        {
            int instructionCycles = machine.Step();
            cycles += instructionCycles;
            cyclesUntilInterrupt -= instructionCycles;
            if (cyclesUntilInterrupt <= 0)
            {
                machine.TickInterrupt();
                cyclesUntilInterrupt += (int)(machine.Profile.ClockHz / machine.Interrupts.Hertz);
            }
        }
    }

    private static void PressKey(Cp500Machine machine, int row, int column)
    {
        machine.Keyboard.SetKeyState(row, column, pressed: true);
        RunForCycles(machine, 100_000);
        machine.Keyboard.SetKeyState(row, column, pressed: false);
        RunForCycles(machine, 100_000);
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
        Assert.Equal(0xFB, machine.Memory.Read(0x3801));
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
    public void InterruptLatchPresentsHeartbeatAndAcknowledgesOnStatusRead()
    {
        var interrupt = new Cp500InterruptDevice(80);
        int callbacks = 0;
        interrupt.SetInterruptCallback(() => callbacks++);

        interrupt.Tick();

        byte status = interrupt.In(Cp500InterruptDevice.Port);

        Assert.Equal(1, callbacks);
        Assert.True((status & 0x81) == 0x81);
        Assert.False(interrupt.InterruptPending);
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

public sealed class Wd1791ControllerTests
{
    [Fact]
    public void RegistersAtModelThreePortsE0ThroughE3()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        var controller = new Wd1791Controller(images);
        var bus = new IoBus();
        controller.Register(bus);

        bus.Out(0xE1, 2);
        bus.Out(0xE2, 1);
        bus.Out(0xE0, 0x80);

        Assert.Equal(2, controller.Track);
        Assert.True(controller.DataRequest);
        Assert.Equal(controller.Status, bus.In(0xE0));
    }

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

        Assert.Equal(0x58, bus.In(0xF5));
        Assert.Equal(3, controller.SelectedDrive);
        Assert.Equal(1, controller.SelectedSide);
        Assert.True(control.WaitRequested);
        Assert.True(control.WaitActive);

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
}
