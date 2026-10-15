using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500PrinterPortTests
{
    private const byte ReadyStatus = 0x30;
    private const byte BusyStatus = 0xB0;

    [Fact]
    public void ReadyPrinterReportsTheModelIIIReadyPattern()
    {
        var bus = BusWith(new FakePrinter { IsReady = true });

        Assert.Equal(0x30, bus.In(0xF8) & 0xF0);
        Assert.Equal(ReadyStatus, bus.In(0xF8));
    }

    [Fact]
    public void BusyPrinterSetsTheBusyBitOnly()
    {
        var bus = BusWith(new FakePrinter { IsReady = false });

        Assert.Equal(BusyStatus, bus.In(0xF8));
    }

    [Fact]
    public void PrinterWithoutADeviceIsAnAlwaysReadySink()
    {
        var bus = BusWith(printer: null);

        bus.Out(0xF8, (byte)'A');

        Assert.Equal(ReadyStatus, bus.In(0xF8));
    }

    [Theory]
    [InlineData(0xF8)]
    [InlineData(0xF9)]
    [InlineData(0xFA)]
    [InlineData(0xFB)]
    public void StatusAndDataAreMirroredAcrossF8ThroughFB(byte port)
    {
        var printer = new FakePrinter { IsReady = true };
        var bus = BusWith(printer);

        bus.Out(port, (byte)'Z');

        Assert.Equal(ReadyStatus, bus.In(port));
        Assert.Equal(new byte[] { (byte)'Z' }, printer.Written);
    }

    [Fact]
    public void PortsOutsideTheMirrorAreNotClaimed()
    {
        var printer = new FakePrinter { IsReady = true };
        var bus = BusWith(printer);

        bus.Out(0xFC, (byte)'Q');

        Assert.Empty(printer.Written);
    }

    private static IoBus BusWith(IPrinterDevice? printer)
    {
        var bus = new IoBus();
        new Cp500PrinterPort(printer).Register(bus);
        return bus;
    }

    private sealed class FakePrinter : IPrinterDevice
    {
        public bool IsReady { get; set; }

        public List<byte> Written { get; } = new();

        public void WriteChar(byte c) => Written.Add(c);
    }
}

public sealed class Cp500KeyboardDeviceTests
{
    [Theory]
    [InlineData(8, 0, "row")]
    [InlineData(-1, 0, "row")]
    [InlineData(0, 8, "column")]
    [InlineData(0, -1, "column")]
    public void SetKeyStateReportsTheOffendingParameter(int row, int column, string parameter)
    {
        var keyboard = new Cp500KeyboardDevice();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => keyboard.SetKeyState(row, column, pressed: true));

        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void ReleaseAllClearsEveryMatrixSwitch()
    {
        var keyboard = new Cp500KeyboardDevice();
        keyboard.SetKeyState(2, 3, pressed: true);
        keyboard.SetKeyState(7, 0, pressed: true);

        keyboard.ReleaseAll();

        Assert.Equal(0, keyboard.Matrix.Read(0xFF));
    }
}

public sealed class MappedMemoryPageTableTests
{
    [Fact]
    public void RomOverlaysRamAndRejectsWrites()
    {
        var rom = new SwitchableRom(0x0000, 0x4000, 0xC3);
        var ram = new RamMemoryRegion(0x0000, 0x10000);
        var memory = new MappedMemory(rom, ram);

        memory.Write(0x0002, 0x42);

        Assert.Equal(0xC3, memory.Read(0x0002));
        Assert.Equal(0x00, ram.Read(0x0002));
    }

    [Fact]
    public void EarlierRegionsOverlayLaterOnesAcrossPageBoundaries()
    {
        var overlay = new RamMemoryRegion(0x30F0, 0x0120);
        var ram = new RamMemoryRegion(0x0000, 0x10000);
        var memory = new MappedMemory(overlay, ram);

        foreach (ushort address in new ushort[] { 0x30EF, 0x30F0, 0x31FF, 0x3200, 0x320F, 0x3210 })
            memory.Write(address, 0x55);

        Assert.Equal(0x00, ram.Read(0x30F0));
        Assert.Equal(0x00, ram.Read(0x320F));
        Assert.Equal(0x55, ram.Read(0x30EF));
        Assert.Equal(0x55, ram.Read(0x3210));
        Assert.Equal(0x55, memory.Read(0x30F0));
        Assert.Equal(0x55, memory.Read(0x320F));
    }

    [Fact]
    public void UnhandledAddressesReadFFAndIgnoreWrites()
    {
        var ram = new RamMemoryRegion(0x4000, 0x100);
        var memory = new MappedMemory(ram);

        memory.Write(0x8000, 0x12);
        memory.Write(0x40FF, 0x34);

        Assert.Equal(0xFF, memory.Read(0x8000));
        Assert.Equal(0xFF, memory.Read(0x3FFF));
        Assert.Equal(0xFF, memory.Read(0x4100));
        Assert.Equal(0x34, memory.Read(0x40FF));
    }

    [Fact]
    public void DisablingAndReenablingAPageAlignedRegionTakesEffectImmediately()
    {
        var rom = new SwitchableRom(0x0000, 0x4000, 0xC3);
        var ram = new RamMemoryRegion(0x0000, 0x10000);
        var memory = new MappedMemory(rom, ram);

        rom.Enabled = false;
        memory.Write(0x0000, 0x5A);
        memory.Write(0x3FFF, 0x5B);

        Assert.Equal(0x5A, memory.Read(0x0000));
        Assert.Equal(0x5B, memory.Read(0x3FFF));

        rom.Enabled = true;

        Assert.Equal(0xC3, memory.Read(0x0000));
        Assert.Equal(0xC3, memory.Read(0x3FFF));
        Assert.Equal(0x5A, ram.Read(0x0000));
    }

    [Fact]
    public void DisablingAnUnalignedRegionRevealsTheRegionBelowOnlyInsideItsRange()
    {
        var rom = new SwitchableRom(0x0010, 0x0020, 0xC3);
        var ram = new RamMemoryRegion(0x0000, 0x100);
        var memory = new MappedMemory(rom, ram);
        ram.Load(0x0010, new byte[0x20].Select(_ => (byte)0x77).ToArray());

        Assert.Equal(0xC3, memory.Read(0x0010));
        Assert.Equal(0xC3, memory.Read(0x002F));

        rom.Enabled = false;

        Assert.Equal(0x77, memory.Read(0x0010));
        Assert.Equal(0x77, memory.Read(0x002F));
        Assert.Equal(0x00, memory.Read(0x000F));
        Assert.Equal(0x00, memory.Read(0x0030));
    }

    [Fact]
    public void ReEnablingAHigherPriorityRegionReclaimsAPageThatRamHadOwned()
    {
        var rom = new SwitchableRom(0x0000, 0x0100, 0xC3) { Enabled = false };
        var ram = new RamMemoryRegion(0x0000, 0x100);
        var memory = new MappedMemory(rom, ram);

        Assert.Equal(0x00, memory.Read(0x0050));

        rom.Enabled = true;

        Assert.Equal(0xC3, memory.Read(0x0050));
    }

    [Fact]
    public void KeyboardAndVideoRangesOverlayRamLikeTheCp500Map()
    {
        var matrix = new KeyboardMatrix();
        matrix.SetKeyState(0, 1, pressed: true);
        var video = new VideoRamRegion(0x3C00, 0x400);
        var rom = new SwitchableRom(0x0000, 0x4000, 0xC3);
        var ram = new RamMemoryRegion(0x4000, 0x4000);
        var memory = new MappedMemory(new KeyboardMatrixRegion(0x3800, 0x400, matrix), video, rom, ram);

        memory.Write(0x3C10, 0x41);
        memory.Write(0x3800, 0xFF);
        memory.Write(0x4000, 0x99);

        Assert.Equal(0x02, memory.Read(0x3801));
        Assert.Equal(0x41, video.Read(0x3C10));
        Assert.Equal(0xC3, memory.Read(0x37FF));
        Assert.Equal(0x99, memory.Read(0x4000));
    }

    private sealed class SwitchableRom : MemoryRegion
    {
        private readonly byte _fill;
        private bool _enabled = true;

        public SwitchableRom(ushort start, int length, byte fill)
            : base(start, length)
        {
            _fill = fill;
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                RaiseMappingChanged();
            }
        }

        public override bool Handles(ushort address) => _enabled && base.Handles(address);

        public override byte Read(ushort address) => _fill;

        public override void Write(ushort address, byte value)
        {
        }
    }
}
public sealed class Cp500VideoDeviceTests
{
    [Theory]
    [InlineData(0x00, 64, false, false)]
    [InlineData(0x04, 32, true, false)]
    [InlineData(0x08, 64, false, true)]
    [InlineData(0x80, 64, false, false)]
    public void ControlLatchBitsSelectColumnsAndTheAlternateSet(byte control, int columns, bool wide, bool alternate)
    {
        var video = new Cp500VideoDevice();

        video.Out(Cp500VideoDevice.ControlPort, control);

        Assert.Equal(columns, video.VisibleColumns);
        Assert.Equal(wide, video.Is32ColumnMode);
        Assert.Equal(alternate, video.AlternateCharacterSet);
    }

    [Fact]
    public void ControlChangesRaiseOnceAndNeverMutateVram()
    {
        var video = VideoWith(0xC1);
        var changes = new List<byte>();
        video.ControlChanged += changes.Add;

        video.Out(Cp500VideoDevice.ControlPort, 0x0D);
        video.Out(Cp500VideoDevice.ControlPort + 1, 0x0C);
        video.Out(Cp500VideoDevice.ControlPort, 0x0F);

        Assert.Equal(new byte[] { 0x0C }, changes);
        Assert.Equal(0xC1, video.ReadCharacter(0, 0));
    }

    private static Cp500VideoDevice VideoWith(byte code)
    {
        var video = new Cp500VideoDevice();
        video.VideoRam.Write(Cp500VideoDevice.StartAddress, code);
        return video;
    }
}

public sealed class Cp500KeyMapTests
{
    [Fact]
    public void BackspaceIsLeftArrowNotClear()
    {
        var backspace = Cp500KeyMap.Map(Cp500HostKey.Backspace);

        Assert.Equal((6, 5), (backspace.Row, backspace.Column));
        Assert.Equal(Cp500KeyMap.Map(Cp500HostKey.Left), backspace);
    }

    [Theory]
    [InlineData(Cp500HostKey.Enter, 6, 0)]
    [InlineData(Cp500HostKey.Home, 6, 1)]
    [InlineData(Cp500HostKey.Escape, 6, 2)]
    [InlineData(Cp500HostKey.Up, 6, 3)]
    [InlineData(Cp500HostKey.Down, 6, 4)]
    [InlineData(Cp500HostKey.Left, 6, 5)]
    [InlineData(Cp500HostKey.Right, 6, 6)]
    [InlineData(Cp500HostKey.Space, 6, 7)]
    [InlineData(Cp500HostKey.Shift, 7, 0)]
    public void SpecialKeysUseTheModelIIIRowSixLayout(Cp500HostKey key, int row, int column)
    {
        var stroke = Cp500KeyMap.Map(key);

        Assert.Equal((row, column), (stroke.Row, stroke.Column));
        Assert.Equal(Cp500ShiftMode.Host, stroke.Shift);
        Assert.Equal(key == Cp500HostKey.Shift, stroke.IsShift);
    }

    [Fact]
    public void ClearIsReachableFromTheHostKeyboard()
    {
        Assert.Contains(
            Enum.GetValues<Cp500HostKey>(),
            key => Cp500KeyMap.Map(key) is { Row: 6, Column: 1 });
    }

    [Theory]
    [InlineData('A', 0, 1, Cp500ShiftMode.Host)]
    [InlineData('a', 0, 1, Cp500ShiftMode.Host)]
    [InlineData('H', 1, 0, Cp500ShiftMode.Host)]
    [InlineData('W', 2, 7, Cp500ShiftMode.Host)]
    [InlineData('Z', 3, 2, Cp500ShiftMode.Host)]
    [InlineData('@', 0, 0, Cp500ShiftMode.Unshifted)]
    [InlineData('0', 4, 0, Cp500ShiftMode.Unshifted)]
    [InlineData('7', 4, 7, Cp500ShiftMode.Unshifted)]
    [InlineData('8', 5, 0, Cp500ShiftMode.Unshifted)]
    [InlineData('9', 5, 1, Cp500ShiftMode.Unshifted)]
    [InlineData('!', 4, 1, Cp500ShiftMode.Shifted)]
    [InlineData('"', 4, 2, Cp500ShiftMode.Shifted)]
    [InlineData('\'', 4, 7, Cp500ShiftMode.Shifted)]
    [InlineData('(', 5, 0, Cp500ShiftMode.Shifted)]
    [InlineData(')', 5, 1, Cp500ShiftMode.Shifted)]
    [InlineData(':', 5, 2, Cp500ShiftMode.Unshifted)]
    [InlineData('*', 5, 2, Cp500ShiftMode.Shifted)]
    [InlineData(';', 5, 3, Cp500ShiftMode.Unshifted)]
    [InlineData('+', 5, 3, Cp500ShiftMode.Shifted)]
    [InlineData(',', 5, 4, Cp500ShiftMode.Unshifted)]
    [InlineData('<', 5, 4, Cp500ShiftMode.Shifted)]
    [InlineData('-', 5, 5, Cp500ShiftMode.Unshifted)]
    [InlineData('=', 5, 5, Cp500ShiftMode.Shifted)]
    [InlineData('.', 5, 6, Cp500ShiftMode.Unshifted)]
    [InlineData('>', 5, 6, Cp500ShiftMode.Shifted)]
    [InlineData('/', 5, 7, Cp500ShiftMode.Unshifted)]
    [InlineData('?', 5, 7, Cp500ShiftMode.Shifted)]
    [InlineData(' ', 6, 7, Cp500ShiftMode.Host)]
    [InlineData('[', 6, 3, Cp500ShiftMode.Unshifted)]
    public void CharactersMapToTheMatrixSwitchAndShiftTheTrs80Needs(
        char character, int row, int column, Cp500ShiftMode shift)
    {
        Assert.True(Cp500KeyMap.TryMapCharacter(character, out var stroke));

        Assert.Equal(new Cp500KeyStroke(row, column, shift), stroke);
    }

    [Theory]
    [InlineData('~')]
    [InlineData('{')]
    [InlineData('\\')]
    [InlineData(']')]
    [InlineData('^')]
    [InlineData('_')]
    [InlineData('\t')]
    [InlineData('\0')]
    public void CharactersWithoutAKeyAreNotMapped(char character)
    {
        Assert.False(Cp500KeyMap.TryMapCharacter(character, out _));
    }
}

public sealed class Cp500HostKeyboardTests
{
    private const int HostA = 1;
    private const int HostLeftShift = 2;
    private const int HostRightShift = 3;
    private const int HostSemicolon = 4;
    private const int HostNumpadMinus = 5;
    private const int HostMinus = 6;
    private const int HostLeft = 7;

    private readonly Cp500KeyboardDevice _device = new();
    private readonly Cp500HostKeyboard _host;

    public Cp500HostKeyboardTests()
    {
        _host = new Cp500HostKeyboard(_device);
    }

    [Fact]
    public void AKeyStaysPressedUntilItIsReleased()
    {
        _host.KeyDown(HostLeft, Cp500KeyMap.Map(Cp500HostKey.Left));

        Assert.Equal(0x20, _device.Matrix.ReadRow(6));
        _host.KeyDown(HostLeft, Cp500KeyMap.Map(Cp500HostKey.Left));
        Assert.Equal(0x20, _device.Matrix.ReadRow(6));

        Assert.True(_host.KeyUp(HostLeft));
        Assert.Equal(0x00, _device.Matrix.ReadRow(6));
    }

    [Fact]
    public void ReleasingAKeyThatIsNotDownDoesNothing()
    {
        Assert.False(_host.KeyUp(HostA));
    }

    [Fact]
    public void SeveralKeysCanBeHeldAtOnce()
    {
        _host.KeyDown(HostA, Letter('A'));
        _host.KeyDown(HostLeft, Cp500KeyMap.Map(Cp500HostKey.Left));

        Assert.Equal(0x02, _device.Matrix.ReadRow(0));
        Assert.Equal(0x20, _device.Matrix.ReadRow(6));

        _host.KeyUp(HostA);

        Assert.Equal(0x00, _device.Matrix.ReadRow(0));
        Assert.Equal(0x20, _device.Matrix.ReadRow(6));
    }

    [Fact]
    public void HostShiftIsTheMatrixShiftAndLettersFollowIt()
    {
        _host.KeyDown(HostLeftShift, Cp500KeyMap.Map(Cp500HostKey.Shift));
        _host.KeyDown(HostA, Letter('A'));

        Assert.Equal(0x01, _device.Matrix.ReadRow(7));
        Assert.Equal(0x02, _device.Matrix.ReadRow(0));

        _host.KeyUp(HostLeftShift);

        Assert.Equal(0x00, _device.Matrix.ReadRow(7));
        Assert.Equal(0x02, _device.Matrix.ReadRow(0));
    }

    [Fact]
    public void ShiftStaysDownUntilBothHostShiftKeysAreReleased()
    {
        _host.KeyDown(HostLeftShift, Cp500KeyMap.Map(Cp500HostKey.Shift));
        _host.KeyDown(HostRightShift, Cp500KeyMap.Map(Cp500HostKey.Shift));

        _host.KeyUp(HostLeftShift);
        Assert.Equal(0x01, _device.Matrix.ReadRow(7));

        _host.KeyUp(HostRightShift);
        Assert.Equal(0x00, _device.Matrix.ReadRow(7));
    }

    [Fact]
    public void HostShiftedColonIsUnshiftedOnTheTrs80AndShiftReturnsOnRelease()
    {
        _host.KeyDown(HostLeftShift, Cp500KeyMap.Map(Cp500HostKey.Shift));
        _host.KeyDown(HostSemicolon, Mapped(':'));

        Assert.Equal(0x04, _device.Matrix.ReadRow(5));
        Assert.Equal(0x00, _device.Matrix.ReadRow(7));

        _host.KeyUp(HostSemicolon);

        Assert.Equal(0x00, _device.Matrix.ReadRow(5));
        Assert.Equal(0x01, _device.Matrix.ReadRow(7));
    }

    [Fact]
    public void HostUnshiftedPunctuationCanNeedShiftOnTheTrs80()
    {
        _host.KeyDown(HostSemicolon, Mapped('*'));

        Assert.Equal(0x04, _device.Matrix.ReadRow(5));
        Assert.Equal(0x01, _device.Matrix.ReadRow(7));

        _host.KeyUp(HostSemicolon);

        Assert.Equal(0x00, _device.Matrix.ReadRow(5));
        Assert.Equal(0x00, _device.Matrix.ReadRow(7));
    }

    [Fact]
    public void ReleasingOneOfTwoHostKeysOnTheSameSwitchKeepsItPressed()
    {
        _host.KeyDown(HostMinus, Mapped('-'));
        _host.KeyDown(HostNumpadMinus, Mapped('-'));

        _host.KeyUp(HostMinus);
        Assert.Equal(0x20, _device.Matrix.ReadRow(5));

        _host.KeyUp(HostNumpadMinus);
        Assert.Equal(0x00, _device.Matrix.ReadRow(5));
    }

    [Fact]
    public void ReleaseAllClearsEverythingIncludingShift()
    {
        _host.KeyDown(HostLeftShift, Cp500KeyMap.Map(Cp500HostKey.Shift));
        _host.KeyDown(HostA, Letter('A'));

        _host.ReleaseAll();

        Assert.Equal(0, _device.Matrix.Read(0xFF));
        _host.KeyDown(HostA, Letter('A'));
        Assert.Equal(0x00, _device.Matrix.ReadRow(7));
    }

    private static Cp500KeyStroke Letter(char letter) => Mapped(letter);

    private static Cp500KeyStroke Mapped(char character)
    {
        Assert.True(Cp500KeyMap.TryMapCharacter(character, out var stroke));
        return stroke;
    }
}