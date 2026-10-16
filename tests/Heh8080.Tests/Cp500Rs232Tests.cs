using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500Rs232Tests
{
    private const long SampleClock = 800;
    private const long ProfileClock = 2_030_000;

    private static readonly byte[] Spin = [0x18, 0xFE];

    [Fact]
    public void StockMachineReadsFfAndKeepsAPendingTimer()
    {
        var machine = new Cp500Machine(Cp500MachineProfile.Original, Spin);

        for (byte port = Cp500Rs232Device.ModemPort; port <= Cp500Rs232Device.DataPort; port++)
        {
            machine.IoBus.Out(port, 0x00);
            Assert.Equal(0xFF, machine.IoBus.In(port));
        }

        long elapsed = 0;
        while (elapsed < machine.Interrupts.TimerPeriodCycles)
            elapsed += machine.Step();

        Assert.Equal(0xFB, machine.IoBus.In(0xE0));
        Assert.Equal(0xFB, machine.IoBus.In(0xE0));
        Assert.Equal(0x00, machine.IoBus.In(0xEC));
        Assert.Equal(0xFF, machine.IoBus.In(0xE0));
    }

    [Fact]
    public void InstalledBoardWithNoCableReadsTheResetRegisters()
    {
        var machine = Machine(new Rs232Attachment(true, null));

        Assert.True(machine.Profile.Rs232Installed);
        Assert.False(Cp500MachineProfile.Original.Rs232Installed);
        Assert.Equal(0x02, machine.IoBus.In(Cp500Rs232Device.ModemPort));
        Assert.Equal(0xFF, machine.IoBus.In(Cp500Rs232Device.BaudPort));
        Assert.Equal(0x47, machine.IoBus.In(Cp500Rs232Device.ControlPort));
        Assert.Equal(0x00, machine.IoBus.In(Cp500Rs232Device.DataPort));
    }

    [Fact]
    public void AttachmentMustMatchTheProfileFlag()
    {
        Assert.Throws<ArgumentException>(() =>
            new Cp500Machine(Cp500MachineProfile.Original, Spin, serial: new Rs232Attachment(true, null)));
        Assert.Throws<ArgumentException>(() =>
            new Cp500Machine(Cp500MachineProfile.Original, Spin, serial: new Rs232Attachment(false, new ScriptedEndpoint())));
    }

    [Fact]
    public void ApplyIsWhatSetsTheProfileFlag()
    {
        var profile = new Cp500MachineProfile(32, 1) { ClockHz = SampleClock };
        Cp500MachineProfile applied = new Rs232Attachment(true, null).Apply(profile);

        Assert.True(applied.Rs232Installed);
        Assert.False(profile.Rs232Installed);
        Assert.Equal(32, applied.RamSizeKiB);
        Assert.Equal(1, applied.InstalledDrives);
        Assert.Equal(SampleClock, applied.ClockHz);
        Assert.False(Rs232Attachment.None.Apply(applied).Rs232Installed);
    }

    [Fact]
    public void ModemPinsAreActiveHighAndTheReceiverIdlesAtMark()
    {
        var (device, bus, endpoint) = Board();

        Assert.Equal(0x02, bus.In(0xE8));
        Assert.Equal(0xFF, bus.In(0xE9));
        endpoint.Inputs = ModemInputs.Online;
        Assert.Equal(0xE2, bus.In(0xE8));
        endpoint.Inputs = ModemInputs.Online with { Ring = true };
        Assert.Equal(0xF2, bus.In(0xE8));
        device.Out(0xE8, 0xFF);
        Assert.Equal(0xF2, bus.In(0xE8));
    }

    [Fact]
    public void ControlWriteIsIgnoredUntilE8Bit1IsSet()
    {
        var (device, bus, endpoint) = Board();

        device.Out(0xEA, 0x0C);
        device.Out(0xEB, 0xFF);
        device.AdvanceCycles(160);
        Assert.Equal(new byte[] { 0xFF }, endpoint.Written.ToArray());
        Assert.Equal(0x47, bus.In(0xEA));

        device.Out(0xE8, 0x00);
        device.Out(0xEA, 0x00);
        Assert.Equal(0x47, bus.In(0xEA));

        device.Out(0xE8, 0x02);
        device.Out(0xEA, Cp500Rs232Device.ResetControl);
        Assert.Equal(0x47, bus.In(0xEA));
        device.Out(0xEA, 0x0C);
        device.Out(0xEB, 0xFF);
        device.AdvanceCycles(112);
        Assert.Equal(new byte[] { 0xFF, 0x1F }, endpoint.Written.ToArray());
    }

    [Theory]
    [InlineData(0x0C, 112, 0x1F)]
    [InlineData(0x4C, 128, 0x3F)]
    [InlineData(0x2C, 144, 0x7F)]
    [InlineData(0x6C, 160, 0xFF)]
    public void ControlWordSelectsFiveThroughEightDataBits(byte control, int samples, byte masked)
    {
        var (device, _, endpoint) = Board();
        device.Out(0xE8, 0x02);
        device.Out(0xEA, control);
        device.Out(0xEB, 0xFF);
        device.AdvanceCycles(samples);
        Assert.Equal(new byte[] { masked }, endpoint.Written.ToArray());
    }

    [Fact]
    public void BreakHoldsTheTransmitter()
    {
        var (device, bus, endpoint) = Board();
        device.Out(0xE8, 0x02);
        device.Out(0xEA, 0x68);
        device.Out(0xEB, 0xA5);
        device.AdvanceCycles(160);

        Assert.Empty(endpoint.Written);
        Assert.Equal(0x07, bus.In(0xEA));

        device.Out(0xEA, Cp500Rs232Device.ResetControl);
        device.AdvanceCycles(160);
        Assert.Equal(new byte[] { 0xA5 }, endpoint.Written.ToArray());
        Assert.Equal(0x47, bus.In(0xEA));
    }

    [Fact]
    public void TransmitHoldingEmptiesAtTheFirstSampleAndTheByteFollowsTheStopBit()
    {
        var (device, bus, endpoint) = Board();
        device.Out(0xEB, 0xA5);

        Assert.Equal(0x07, bus.In(0xEA));
        device.AdvanceCycles(1);
        Assert.Equal(0x47, bus.In(0xEA));
        Assert.Empty(endpoint.Written);

        device.AdvanceCycles(158);
        Assert.Empty(endpoint.Written);
        device.AdvanceCycles(1);
        Assert.Equal(new byte[] { 0xA5 }, endpoint.Written.ToArray());
    }

    [Fact]
    public void E8WriteDoesNotResetTheBaudLatch()
    {
        var (device, bus, _) = Board(ProfileClock);
        bus.Out(0xE9, 0xE0);
        bus.Out(0xEB, 0x99);
        bus.Out(0xE8, 0x00);
        Assert.Equal(0x07, bus.In(0xEA));

        device.AdvanceCycles(CyclesForSamples(ProfileClock, 9_600_000, 1));
        Assert.Equal(0x47, bus.In(0xEA));
    }

    [Fact]
    public void OverrunKeepsTheFirstByteUntilTheNextStopBit()
    {
        var (device, bus, endpoint) = Board();
        endpoint.Enqueue(0x11);
        endpoint.Enqueue(0x22);

        device.AdvanceCycles(311);
        Assert.Equal(0xC7, bus.In(0xEA));
        device.AdvanceCycles(1);
        Assert.Equal(0xE7, bus.In(0xEA));
        Assert.Equal(0xE7, bus.In(0xEA));
        Assert.Equal(0x11, bus.In(0xEB));
        Assert.Equal(0x67, bus.In(0xEA));

        endpoint.Enqueue(0x33);
        device.AdvanceCycles(160);
        Assert.Equal(0xC7, bus.In(0xEA));
        Assert.Equal(0x33, bus.In(0xEB));
        Assert.Equal(0x47, bus.In(0xEA));
    }

    [Fact]
    public void BreakSetsFramingAndTheDataReadClearsOnlyDav()
    {
        var (device, bus, endpoint) = Board();
        endpoint.Inputs = ModemInputs.Idle with { ReceiveMark = false };

        device.AdvanceCycles(151);
        Assert.Equal(0x47, bus.In(0xEA));
        device.AdvanceCycles(1);
        Assert.Equal(0xD7, bus.In(0xEA));
        Assert.Equal(0x00, bus.In(0xE8) & 0x02);
        Assert.Equal(0x00, bus.In(0xEB));
        Assert.Equal(0x57, bus.In(0xEA));

        endpoint.Inputs = ModemInputs.Idle;
        endpoint.Enqueue(0x55);
        device.AdvanceCycles(159);
        Assert.Equal(0x57, bus.In(0xEA));
        device.AdvanceCycles(1);
        Assert.Equal(0xC7, bus.In(0xEA));
        Assert.Equal(0x55, bus.In(0xEB));
        Assert.Equal(0x47, bus.In(0xEA));
    }

    [Fact]
    public void HighNibbleClocksTransmitAndLowNibbleClocksReceive()
    {
        var (device, bus, endpoint) = Board(ProfileClock);
        bus.Out(0xE9, 0xE0);
        endpoint.Enqueue(0x42);
        bus.Out(0xEB, 0x99);

        long oneTransmitSample = CyclesForSamples(ProfileClock, 9_600_000, 1);
        Assert.Equal(14, oneTransmitSample);
        device.AdvanceCycles(oneTransmitSample - 1);
        Assert.Equal(0x07, bus.In(0xEA));
        Assert.Equal(0x02, bus.In(0xE8));

        device.AdvanceCycles(1);
        Assert.Equal(0x47, bus.In(0xEA));
        Assert.Equal(0x02, bus.In(0xE8));
        Assert.Empty(endpoint.Written);
        Assert.Equal(0x00, bus.In(0xEB));
    }

    [Theory]
    [InlineData(0x33, 134_500, 150930)]
    [InlineData(0xEE, 9_600_000, 2115)]
    public void ProgrammedBaudShiftsAByteOnTheMillibaudTable(byte latch, int millibaud, long cycles)
    {
        var (device, bus, endpoint) = Board(ProfileClock);
        Assert.Equal(cycles, CyclesForSamples(ProfileClock, millibaud, 160));

        bus.Out(0xE9, latch);
        bus.Out(0xEB, 0x5A);
        device.AdvanceCycles(cycles - 1);
        Assert.Empty(endpoint.Written);
        device.AdvanceCycles(1);
        Assert.Equal(new byte[] { 0x5A }, endpoint.Written.ToArray());
    }

    [Fact]
    public void StepClocksTheTransmitterByTheInstructionLength()
    {
        var endpoint = new ScriptedEndpoint();
        var machine = Machine(new Rs232Attachment(true, endpoint), SampleClock);
        machine.IoBus.Out(0xEB, 0xA5);

        int cycles = machine.Step();
        Assert.Equal(12, cycles);
        Assert.Equal(0x47, machine.IoBus.In(0xEA));
        Assert.Empty(endpoint.Written);

        for (int i = 0; i < 12; i++)
            machine.Step();
        Assert.Empty(endpoint.Written);

        machine.Step();
        Assert.Equal(new byte[] { 0xA5 }, endpoint.Written.ToArray());
    }

    [Fact]
    public void E0Bit5FollowsDataReceivedAndTheReadLeavesTheTimerPending()
    {
        var endpoint = new ScriptedEndpoint();
        var machine = Machine(new Rs232Attachment(true, endpoint), SampleClock);
        machine.IoBus.Out(0xE0, (byte)(Cp500InterruptDevice.IrqTimer | Cp500InterruptDevice.IrqRs232Receive));
        endpoint.Enqueue(0x42);

        Assert.Equal(0xFF, machine.IoBus.In(0xE0));
        machine.Step();
        machine.Step();
        machine.Step();
        Assert.Equal(0xFB, machine.IoBus.In(0xE0));

        for (int i = 0; i < 10; i++)
            machine.Step();

        Assert.Equal(0xDB, machine.IoBus.In(0xE0));
        Assert.Equal(0xDB, machine.IoBus.In(0xE0));
        Assert.Equal(0x00, machine.IoBus.In(0xEC));
        Assert.Equal(0xDF, machine.IoBus.In(0xE0));
        Assert.Equal(0x42, machine.IoBus.In(0xEB));
        Assert.Equal(0xFF, machine.IoBus.In(0xE0));
    }

    [Fact]
    public void ADriverPollsStatusAndReadsTheEchoedByte()
    {
        byte[] rom =
        [
            0x3E, 0xFF,             // LD A,FFh
            0xD3, 0xE9,             // OUT (E9h),A          ; 19200 baud both ways
            0xDB, 0xEA,             // IN A,(EAh)
            0xCB, 0x77,             // BIT 6,A              ; TBMT
            0x28, 0xFA,             // JR Z, tbmt
            0x3E, 0x5A,             // LD A,5Ah
            0xD3, 0xEB,             // OUT (EBh),A
            0xDB, 0xEA,             // IN A,(EAh)
            0xCB, 0x7F,             // BIT 7,A              ; DAV
            0x28, 0xFA,             // JR Z, dav
            0xDB, 0xEB,             // IN A,(EBh)
            0x32, 0x00, 0x40,       // LD (4000h),A
            0x18, 0xFE,             // JR $
        ];
        var endpoint = new EchoEndpoint();
        var serial = new Rs232Attachment(true, endpoint);
        var machine = new Cp500Machine(serial.Apply(Cp500MachineProfile.Original), rom, serial: serial);

        long elapsed = 0;
        while (machine.Memory.Read(0x4000) == 0 && elapsed < 100_000)
            elapsed += machine.Step();

        Assert.Equal(0x5A, machine.Memory.Read(0x4000));
    }

    [Fact]
    public void E0Bit4FollowsTransmitHoldingEmpty()
    {
        var machine = Machine(new Rs232Attachment(true, null), SampleClock);
        machine.IoBus.Out(0xE0, Cp500InterruptDevice.IrqRs232Transmit);

        Assert.Equal(0xEF, machine.IoBus.In(0xE0));
        machine.IoBus.Out(0xEB, 0x11);
        Assert.Equal(0xFF, machine.IoBus.In(0xE0));
        machine.Step();
        Assert.Equal(0xEF, machine.IoBus.In(0xE0));
    }

    private static Cp500Machine Machine(Rs232Attachment serial, long clockHz = ProfileClock)
    {
        var profile = new Cp500MachineProfile { ClockHz = clockHz };
        return new Cp500Machine(serial.Apply(profile), Spin, serial: serial);
    }

    private static (Cp500Rs232Device Device, IoBus Bus, ScriptedEndpoint Endpoint) Board(long clockHz = SampleClock)
    {
        var endpoint = new ScriptedEndpoint();
        var device = new Cp500Rs232Device(clockHz, endpoint);
        var bus = new IoBus();
        device.Register(bus);
        return (device, bus, endpoint);
    }

    private static long CyclesForSamples(long clockHz, int millibaud, int samples)
    {
        long span = clockHz * 1000L;
        long step = (long)millibaud * 16L;
        return (samples * span + step - 1) / step;
    }

    private sealed class EchoEndpoint : ISerialEndpoint
    {
        private readonly Queue<byte> _incoming = new();

        public ModemInputs Inputs => ModemInputs.Idle;

        public bool TryRead(out byte value)
        {
            if (_incoming.Count == 0)
            {
                value = 0;
                return false;
            }

            value = _incoming.Dequeue();
            return true;
        }

        public void Write(byte value) => _incoming.Enqueue(value);

        public void AdvanceCycles(long cycles)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedEndpoint : ISerialEndpoint
    {
        private readonly Queue<byte> _incoming = new();

        public List<byte> Written { get; } = [];

        public ModemInputs Inputs { get; set; } = ModemInputs.Idle;

        public void Enqueue(byte value) => _incoming.Enqueue(value);

        public bool TryRead(out byte value)
        {
            if (_incoming.Count == 0)
            {
                value = 0;
                return false;
            }

            value = _incoming.Dequeue();
            return true;
        }

        public void Write(byte value) => Written.Add(value);

        public void AdvanceCycles(long cycles)
        {
        }

        public void Dispose()
        {
        }
    }
}
