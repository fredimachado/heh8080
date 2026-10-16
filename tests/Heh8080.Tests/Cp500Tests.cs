using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class MappedMemoryTests
{
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
    public void KeyboardMatrixReportsWhetherAnyKeyIsPressed()
    {
        var matrix = new KeyboardMatrix();
        Assert.False(matrix.AnyPressed);

        matrix.SetKeyState(7, 7, pressed: true);
        Assert.True(matrix.AnyPressed);

        matrix.SetKeyState(7, 7, pressed: false);
        Assert.False(matrix.AnyPressed);
    }
}

public sealed class Cp500MachineTests
{
    [RealRomFact]
    public void RealRomWithEmptyDrivesWaitsForADisketteAndIgnoresBreak()
    {
        var session = new RealRomSession();

        Assert.True(session.RunUntilScreenContains("Diskette?", maxSeconds: 5));

        // The ROM's disk-wait loop never reads the keyboard: it retries until an index pulse appears.
        session.Press(row: 6, column: 2, heldSeconds: 6);
        session.Run(seconds: 3);
        string screen = session.Screen();

        Assert.Contains("Diskette?", screen);
        Assert.DoesNotContain("BASIC", screen);
    }

    [RealRomFact]
    public void RealRomWithEmptyDrivesResetHoldingBreakReachesBasic()
    {
        var session = new RealRomSession();
        Assert.True(session.RunUntilScreenContains("Diskette?", maxSeconds: 5));

        session.Machine.ResetHoldingBreak();

        Assert.True(session.RunUntilScreenContains("BASIC (S ou N)", maxSeconds: 10));
        Assert.DoesNotContain("Diskette?", session.Screen());

        session.Type("S\r\r");
        session.Run(seconds: 3);
        session.Type("PRINT 22\r");
        session.Run(seconds: 1);
        string basic = session.Screen();

        Assert.Contains("READY", basic);
        Assert.Contains("\n 22", "\n" + basic);
    }

    [Fact]
    public void ResetHoldingBreakHoldsBreakForOneSecondOfEmulatedTimeThenReleasesIt()
    {
        var machine = SpinningMachine();

        machine.ResetHoldingBreak();

        Assert.True(BreakIsDown(machine));
        RunCycles(machine, machine.Profile.ClockHz - 100);
        Assert.True(BreakIsDown(machine));
        RunCycles(machine, 200);
        Assert.False(BreakIsDown(machine));
    }

    [Fact]
    public void PlainResetCancelsTheBreakHold()
    {
        var machine = SpinningMachine();
        machine.ResetHoldingBreak();

        machine.Reset();

        Assert.False(BreakIsDown(machine));
    }

    [Fact]
    public void TheBreakHoldNeitherClobbersNorIsReleasedByHostKeys()
    {
        var machine = SpinningMachine();
        var host = new Cp500HostKeyboard(machine.Keyboard);
        Cp500KeyStroke escape = Cp500KeyMap.Map(Cp500HostKey.Escape);

        machine.ResetHoldingBreak();
        host.KeyDown(1, escape);
        host.KeyUp(1);
        host.ReleaseAll();
        machine.Keyboard.ReleaseAll();
        Assert.True(BreakIsDown(machine));

        host.KeyDown(1, escape);
        RunCycles(machine, machine.Profile.ClockHz + 100);
        Assert.True(BreakIsDown(machine));

        host.KeyUp(1);
        Assert.False(BreakIsDown(machine));
    }

    [Fact]
    public void SpeedFactorSurvivesReset()
    {
        var machine = SpinningMachine();

        machine.SpeedFactor = 4;
        machine.Reset();
        machine.ResetHoldingBreak();

        Assert.Equal(4, machine.SpeedFactor);
    }

    [Fact]
    public void UnlimitedSpeedNeverSuggestsADelay()
    {
        var machine = SpinningMachine();
        machine.SpeedFactor = double.PositiveInfinity;

        RunCycles(machine, machine.Profile.ClockHz);

        Assert.Equal(TimeSpan.Zero, machine.LastSuggestedDelay);
    }

    [Fact]
    public void AHeldHostKeyPacesAFastForwardedMachineAtRealTimeUntilReleased()
    {
        var machine = SpinningMachine();
        machine.SpeedFactor = double.PositiveInfinity;

        machine.Keyboard.SetKeyState(1, 1, pressed: true);
        RunCycles(machine, machine.Profile.ClockHz / 4);
        Assert.True(machine.LastSuggestedDelay > TimeSpan.FromMilliseconds(100));

        machine.Keyboard.SetKeyState(1, 1, pressed: false);
        RunCycles(machine, machine.Profile.ClockHz);
        Assert.Equal(TimeSpan.Zero, machine.LastSuggestedDelay);
        Assert.Equal(double.PositiveInfinity, machine.SpeedFactor);
    }

    [Fact]
    public void TheAutomaticBreakHoldDoesNotSlowFastForward()
    {
        var machine = SpinningMachine();
        machine.SpeedFactor = double.PositiveInfinity;

        machine.ResetHoldingBreak();
        RunCycles(machine, machine.Profile.ClockHz / 4);

        Assert.True(BreakIsDown(machine));
        Assert.Equal(TimeSpan.Zero, machine.LastSuggestedDelay);
    }

    private static Cp500Machine SpinningMachine()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        rom[0] = 0x18;
        rom[1] = 0xFE; // JR $
        return new Cp500Machine(Cp500MachineProfile.Original, rom);
    }

    private static void RunCycles(Cp500Machine machine, long cycles)
    {
        for (long executed = 0; executed < cycles;)
            executed += machine.Step();
    }

    private static bool BreakIsDown(Cp500Machine machine) => (machine.Memory.Read(0x3840) & 0x04) != 0;

    [RealRomFact]
    public void RealRomWithoutDiskDrivesBootsToBasicAndRunsAStatement()
    {
        var session = new RealRomSession(profile: new Cp500MachineProfile(installedDrives: 0));

        Assert.True(session.RunUntilScreenContains("BASIC (S ou N)", maxSeconds: 10));
        string boot = session.Screen();

        Assert.Contains("PROLOGICA CP-500", boot);
        Assert.DoesNotContain("Cass?", boot);
        Assert.DoesNotContain("Diskette?", boot);

        session.Type("S\r\r");
        session.Run(seconds: 3);
        session.Type("PRINT 22\r");
        session.Run(seconds: 1);
        string basic = session.Screen();

        Assert.Contains("READY", basic);
        Assert.Contains("\n 22", "\n" + basic);
    }

    [RealRomTheory]
    [InlineData(BlankDisk.Formatted)]
    [InlineData(BlankDisk.Unformatted)]
    public void RealRomLoadsTheBootSectorOfAnInsertedDisketteWithoutALoader(BlankDisk kind)
    {
        // Neither disk holds a boot loader, so the ROM jumps into filler (E5h or 00h) and the machine is lost,
        // but it did find the disk: index pulses, boot sector read, and the jump to 4300h.
        var images = new MemoryModelIIIDiskImageProvider();
        var session = new RealRomSession(images);
        Assert.True(session.RunUntilScreenContains("Diskette?", maxSeconds: 5));

        MountBlankDisk(images, kind);

        Assert.True(session.RunUntilProgramCounter(BootSectorAddress, maxSeconds: 10));
    }

    [RealRomFact]
    public void RealRomRunsTheBootSectorOfAnInsertedDiskette()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        var session = new RealRomSession(images);
        Assert.True(session.RunUntilScreenContains("Diskette?", maxSeconds: 5));

        images.Mount(0, BootSectorPrinting("BOOT"), ModelIIIDiskImageKind.Raw);

        Assert.True(session.RunUntilScreenContains("BOOT", maxSeconds: 10));
    }

    [RealRomFact]
    public void RealRomEchoesEveryMappedCharacterAsItself()
    {
        var session = new RealRomSession(profile: new Cp500MachineProfile(installedDrives: 0));
        Assert.True(session.RunUntilScreenContains("BASIC (S ou N)", maxSeconds: 10));
        session.Type("S\r\r");
        session.Run(seconds: 3);
        var mismatches = new List<string>();

        for (char character = '!'; character <= '~'; character++)
        {
            if (!Cp500KeyMap.TryMapCharacter(character, out _))
                continue;

            session.Press(row: 6, column: 1);
            session.Run(seconds: 1.5);
            session.Type(character.ToString());
            char expected = char.ToUpperInvariant(character);
            if (!session.Screen().Contains(expected))
                mismatches.Add($"'{character}' did not echo as '{expected}'");
        }

        Assert.Empty(mismatches);
    }

    [RealRomFact]
    public void RealRomEchoesEveryCharacterTypedWithUsKeyboardShiftThroughTheHostKeyboard()
    {
        var session = BasicReadySession();
        var host = new Cp500HostKeyboard(session.Machine.Keyboard);
        var mismatches = new List<string>();

        for (char character = '!'; character <= '~'; character++)
        {
            if (!Cp500KeyMap.TryMapCharacter(character, out _))
                continue;

            session.Press(row: 6, column: 1);
            session.Run(seconds: 1.5);
            TypeLikeAUsKeyboard(session, host, character.ToString());
            string expected = char.ToUpperInvariant(character).ToString();
            string shown = session.Screen().Trim();
            if (shown != expected)
                mismatches.Add($"'{character}' showed \"{shown}\"");
        }

        Assert.Empty(mismatches);
    }

    [RealRomFact]
    public void RealRomBackspaceAfterAShiftedCharacterErasesOnlyThatCharacter()
    {
        var session = BasicReadySession();
        var host = new Cp500HostKeyboard(session.Machine.Keyboard);

        TypeLikeAUsKeyboard(session, host, "PRINT (1+2)");
        host.KeyDown(HostBackspace, Cp500KeyMap.Map(Cp500HostKey.Backspace));
        session.Run(seconds: 0.1);
        host.KeyUp(HostBackspace);
        session.Run(seconds: 0.5);

        Assert.Contains(">PRINT (1+2\n", session.Screen() + "\n");
    }

    private const int HostShift = 1;
    private const int HostCharacter = 2;
    private const int HostBackspace = 3;
    private const string UsShiftedCharacters = "~!@#$%^&*()_+{}|:\"<>?";

    private static RealRomSession BasicReadySession()
    {
        var session = new RealRomSession(profile: new Cp500MachineProfile(installedDrives: 0));
        Assert.True(session.RunUntilScreenContains("BASIC (S ou N)", maxSeconds: 10));
        session.Type("S\r\r");
        session.Run(seconds: 3);
        return session;
    }

    /// <summary>Holds host SHIFT around each character a US keyboard needs it for, as a user's fingers do.</summary>
    private static void TypeLikeAUsKeyboard(RealRomSession session, Cp500HostKeyboard host, string text)
    {
        foreach (char character in text)
        {
            Assert.True(Cp500KeyMap.TryMapCharacter(character, out var stroke));
            bool shifted = UsShiftedCharacters.Contains(character) || char.IsUpper(character);
            if (shifted)
                host.KeyDown(HostShift, Cp500KeyMap.Map(Cp500HostKey.Shift));
            host.KeyDown(HostCharacter, stroke);
            session.Run(seconds: 0.1);
            host.KeyUp(HostCharacter);
            if (shifted)
                host.KeyUp(HostShift);
            session.Run(seconds: 0.5);
        }
    }

    public enum BlankDisk
    {
        Formatted,
        Unformatted
    }

    private const ushort BootSectorAddress = 0x4300;

    private static void MountBlankDisk(MemoryModelIIIDiskImageProvider images, BlankDisk kind)
    {
        if (kind == BlankDisk.Formatted)
            images.MountBlank(0);
        else
            images.Mount(0, new byte[ModelIIIDiskGeometry.DiskSizeBytes], ModelIIIDiskImageKind.Raw);
    }

    /// <summary>A raw disk whose boot sector copies <paramref name="text"/> to video row 8 and spins.</summary>
    private static byte[] BootSectorPrinting(string text)
    {
        const int textOffset = 13;
        byte[] program =
        [
            0x21, textOffset, 0x43,                    // LD HL,text
            0x11, 0x00, 0x3E,                          // LD DE,3E00h (video row 8)
            0x01, (byte)text.Length, 0x00,             // LD BC,length
            0xED, 0xB0,                                // LDIR
            0x18, 0xFE                                 // JR $
        ];
        byte[] disk = new byte[ModelIIIDiskGeometry.DiskSizeBytes];
        program.CopyTo(disk, 0);
        System.Text.Encoding.ASCII.GetBytes(text).CopyTo(disk, textOffset);
        return disk;
    }

    [Fact]
    public void OriginalProfileMapsDevicesRomRamAndOpenBus()
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        rom[0] = 0xC3;
        var machine = new Cp500Machine(new Cp500MachineProfile(16), rom);
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
    public void ClockAddressesBehaveAsPlainRam()
    {
        var machine = new Cp500Machine(Cp500MachineProfile.Original, new byte[Cp500MachineProfile.SystemRomSizeBytes]);

        foreach (ushort address in new ushort[] { 0x4040, 0x4041, 0x4042 })
        {
            machine.Memory.Write(address, 0xA5);
            Assert.Equal(0xA5, machine.Memory.Read(address));
        }
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

        machine.Interrupts.AdvanceCycles(machine.Interrupts.TimerPeriodCycles);

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

        machine.Interrupts.AdvanceCycles(machine.Interrupts.TimerPeriodCycles);

        Assert.Equal(0xFF, machine.IoBus.In(0xE0));
    }

    [Fact]
    public void FfIsACassetteStubThatLeavesInterruptsAlone()
    {
        var machine = CreateMachine();
        machine.IoBus.Out(0xE0, 0x04);
        machine.Interrupts.AdvanceCycles(machine.Interrupts.TimerPeriodCycles);

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
        machine.IoBus.Out(0xF4, 0x81);

        machine.IoBus.Out(0xF1, 2);
        machine.IoBus.Out(0xF2, 1);

        Assert.Equal(2, machine.IoBus.In(0xF1));
        Assert.Equal(1, machine.IoBus.In(0xF2));

        machine.IoBus.Out(0xF1, 0);
        machine.IoBus.Out(0xF0, 0x80);

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
        Assert.Equal(0x7F, machine.IoBus.In(0xE7));

        machine.IoBus.In(0xF0);

        Assert.Equal(0xFF, machine.IoBus.In(0xE4));
    }

    [Fact]
    public void OnlyTheProfilesInstalledDrivesReportTrackZero()
    {
        var machine = new Cp500Machine(
            new Cp500MachineProfile(installedDrives: 1), new byte[Cp500MachineProfile.SystemRomSizeBytes]);

        machine.IoBus.Out(0xF4, 0x01);
        Assert.Equal(0x04, machine.IoBus.In(0xF0) & 0x04);

        machine.IoBus.Out(0xF4, 0x02);
        Assert.Equal(0x00, machine.IoBus.In(0xF0) & 0x04);
        Assert.Equal(2, Cp500MachineProfile.Original.InstalledDrives);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Cp500MachineProfile(installedDrives: 5));
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

public sealed partial class Wd1791ControllerTests
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
        controller.DataRequestChanged += drqChanges.Add;
        controller.InterruptRequestChanged += intrqChanges.Add;

        bus.Out(0xF7, 0xD8);

        Assert.Equal(3, controller.SelectedDrive);
        Assert.Equal(1, controller.SelectedSide);
        Assert.Equal(ModelIIIDiskDensity.DoubleDensity, controller.Density);
        Assert.True(control.WaitRequested);
        Assert.False(control.WaitActive);

        bus.Out(0xF2, 1);
        bus.Out(0xF0, 0x80);

        Assert.True(controller.DataRequest);
        Assert.False(control.WaitActive);
        Assert.Equal(new[] { true }, drqChanges);

        for (int index = 0; index < ModelIIIDiskGeometry.BytesPerSector; index++)
            bus.In(0xF3);

        Assert.True(controller.InterruptRequest);
        Assert.Contains(true, intrqChanges);
    }

    [Fact]
    public void DefaultImagesAreSingleSidedAndDoubleSidedImagesAreExplicit()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        byte[] sector = Enumerable.Repeat((byte)0x5A, ModelIIIDiskGeometry.BytesPerSector).ToArray();

        images.MountBlank(0);
        Assert.True(images.WriteSector(0, 39, 0, 18, sector));
        Assert.Empty(images.GetTrack(0, 0, 1));

        images.MountBlank(0, ModelIIIDiskFormat.DoubleSided);
        Assert.True(images.WriteSector(0, 0, 1, 1, sector));
        Assert.Equal(sector, images.GetTrack(0, 0, 1).Single(value => value.Id == 1).Data);
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

        Assert.Equal(11, controller.Track);
        Assert.Equal(2, controller.PhysicalTrack);

        controller.Out(Wd1791Controller.DataPort, 17);
        controller.Out(Wd1791Controller.CommandStatusPort, 0x10);
        Assert.Equal(17, controller.Track);
        Assert.Equal(8, controller.PhysicalTrack);

        controller.Out(Wd1791Controller.CommandStatusPort, 0x60);
        Assert.Equal(17, controller.Track);
        Assert.Equal(7, controller.PhysicalTrack);

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

}
