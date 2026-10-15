using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500InterruptDeviceTests
{
    private const long Period = 100;

    private readonly Cp500InterruptDevice _device = new(Cp500InterruptDevice.TimerHertz * Period);
    private readonly List<bool> _lineChanges = new();
    private int _nmiCount;

    private bool Line => _lineChanges.Count > 0 && _lineChanges[^1];

    public Cp500InterruptDeviceTests()
    {
        _device.InterruptLineChanged += _lineChanges.Add;
        _device.NonMaskableInterruptRaised += () => _nmiCount++;
    }

    [Fact]
    public void TimerAssertsTheIrqLevelEveryPeriodAndEcReleasesIt()
    {
        _device.AdvanceCycles(Period - 1);
        Assert.False(Line);

        _device.AdvanceCycles(1);
        Assert.True(Line);

        _device.In(0xEC);
        Assert.False(Line);
        Assert.Equal(new[] { true, false }, _lineChanges);
    }

    [Fact]
    public void IrqLineFollowsTheMask()
    {
        _device.AdvanceCycles(Period);

        _device.Out(0xE0, 0x00);
        Assert.False(Line);

        _device.Out(0xE0, Cp500InterruptDevice.IrqTimer);
        Assert.True(Line);
    }

    [Fact]
    public void TimerTickWhileMaskedIsHeldUntilEnabled()
    {
        _device.Out(0xE0, 0x00);
        _device.AdvanceCycles(Period);

        Assert.Equal(0xFF, _device.In(0xE0));
        Assert.False(Line);

        _device.Out(0xE0, Cp500InterruptDevice.IrqTimer);

        Assert.Equal(0xFB, _device.In(0xE0));
        Assert.True(Line);
    }

    [Fact]
    public void TimerDeadlinesDoNotDriftWhenALargeStepSpansSeveralPeriods()
    {
        _device.AdvanceCycles(250);
        _device.In(0xEC);

        _device.AdvanceCycles(49);
        Assert.False(Line);

        _device.AdvanceCycles(1);
        Assert.True(Line);
    }

    [Fact]
    public void FdcIntrqStatusFollowsTheLiveLineAndRaisesNmiOnlyOnEnabledRisingEdges()
    {
        _device.SetFdcInterruptRequest(true);
        Assert.Equal(0, _nmiCount);

        _device.Out(0xE4, Cp500InterruptDevice.NmiFdcIntrq);
        Assert.Equal(1, _nmiCount);

        Assert.Equal(0x7F, _device.In(0xE4));
        Assert.Equal(0x7F, _device.In(0xE4));

        _device.SetFdcInterruptRequest(false);
        Assert.Equal(0xFF, _device.In(0xE4));

        _device.SetFdcInterruptRequest(true);
        Assert.Equal(2, _nmiCount);
    }

    [Fact]
    public void ResetReturnsToTheTimerOnlyMaskWithLinesLow()
    {
        _device.AdvanceCycles(Period);
        _device.Out(0xE4, Cp500InterruptDevice.NmiFdcIntrq);
        _device.SetFdcInterruptRequest(true);

        _device.Reset();

        Assert.False(Line);
        Assert.Equal(0xFF, _device.In(0xE0));
        Assert.Equal(0xFF, _device.In(0xE4));
        Assert.False(_lineChanges[^1]);
    }

    [Fact]
    public void RejectsANonPositiveClock()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Cp500InterruptDevice(0));
    }
}

public sealed class Cp500MachineInterruptTests
{
    private const ushort TickCounter = 0x4000;
    private const ushort Release = 0x4001;
    private const ushort AfterInir = 0x4002;
    private const ushort NmiCounter = 0x4010;

    // IN A,(ECh) ; LD HL,TickCounter ; INC (HL) ; EI ; RET
    private static readonly byte[] TimerHandler = { 0xDB, 0xEC, 0x21, 0x00, 0x40, 0x34, 0xFB, 0xC9 };

    // IN A,(F0h) ; LD DE,NmiCounter ; LD A,(DE) ; INC A ; LD (DE),A ; RETN
    private static readonly byte[] NmiHandler = { 0xDB, 0xF0, 0x11, 0x10, 0x40, 0x1A, 0x3C, 0x12, 0xED, 0x45 };

    private static byte[] Rom(byte[] main, byte[]? irq = null, byte[]? nmi = null)
    {
        byte[] rom = new byte[Cp500MachineProfile.SystemRomSizeBytes];
        main.CopyTo(rom, 0x0000);
        irq?.CopyTo(rom, 0x0038);
        nmi?.CopyTo(rom, 0x0066);
        return rom;
    }

    private static void Run(Cp500Machine machine, long cycles)
    {
        for (long elapsed = 0; elapsed < cycles;)
            elapsed += machine.Step();
    }

    private static long Period(Cp500Machine machine) => machine.Interrupts.TimerPeriodCycles;

    private static Func<bool> IrqLine(Cp500Machine machine)
    {
        bool level = false;
        machine.Interrupts.InterruptLineChanged += asserted => level = asserted;
        return () => level;
    }

    [Fact]
    public void StepAdvancesTheTimerByTheExecutedCycles()
    {
        // JR $
        var machine = new Cp500Machine(Cp500MachineProfile.Original, Rom(new byte[] { 0x18, 0xFE }));
        var irq = IrqLine(machine);

        Run(machine, Period(machine) - 12);
        Assert.False(irq());

        Run(machine, 12);
        Assert.True(irq());
    }

    [Fact]
    public void TimerInterruptsArriveOncePerPeriodOfEmulatedTime()
    {
        // EI ; JR $
        var machine = new Cp500Machine(
            Cp500MachineProfile.Original,
            Rom(new byte[] { 0xFB, 0x18, 0xFE }, TimerHandler));

        Run(machine, Period(machine) * 5 / 2);

        Assert.Equal(2, machine.Memory.Read(TickCounter));
    }

    [Fact]
    public void Mode2TimerInterruptVectorsThroughTheTableEntryAtTheFloatingBusByteFFh()
    {
        // LD A,80h ; LD I,A ; IM 2 ; EI ; JR $
        byte[] main = { 0x3E, 0x80, 0xED, 0x47, 0xED, 0x5E, 0xFB, 0x18, 0xFE };
        var machine = new Cp500Machine(Cp500MachineProfile.Original, Rom(main));
        // 80FEh:80FFh reads 5000h, 80FFh:8100h reads 6050h.
        machine.Memory.Write(0x80FE, 0x00);
        machine.Memory.Write(0x80FF, 0x50);
        machine.Memory.Write(0x8100, 0x60);
        machine.Memory.Write(0x5000, 0x18);
        machine.Memory.Write(0x5001, 0xFE);
        for (int index = 0; index < TimerHandler.Length; index++)
            machine.Memory.Write((ushort)(0x6050 + index), TimerHandler[index]);

        Run(machine, Period(machine) * 5 / 2);

        Assert.Equal(2, machine.Memory.Read(TickCounter));
    }

    [Fact]
    public void TimerTickRaisedDuringDiIsServicedOnceInterruptsAreEnabled()
    {
        byte[] main =
        {
            0xF3,                   // 0000 DI
            0x3A, 0x01, 0x40,       // 0001 LD A,(Release)
            0xB7,                   // 0004 OR A
            0x28, 0xFA,             // 0005 JR Z,0001
            0xFB,                   // 0007 EI
            0x18, 0xFE              // 0008 JR $
        };
        var machine = new Cp500Machine(Cp500MachineProfile.Original, Rom(main, TimerHandler));
        var irq = IrqLine(machine);

        Run(machine, Period(machine) * 3 / 2);

        Assert.True(irq());
        Assert.Equal(0, machine.Memory.Read(TickCounter));

        machine.Memory.Write(Release, 1);
        Run(machine, 200);

        Assert.Equal(1, machine.Memory.Read(TickCounter));
        Assert.False(irq());
    }

    [Fact]
    public void FdcNmiRaisedByTheLastDataByteOfInirIsTakenAfterTheInstruction()
    {
        var images = new MemoryModelIIIDiskImageProvider();
        images.MountBlank(0);
        byte[] main =
        {
            0x3E, 0x01, 0xD3, 0xF4,         // OUT (F4),01 select drive 0
            0x3E, 0x80, 0xD3, 0xE4,         // OUT (E4),80 enable FDC INTRQ NMI
            0x3E, 0x01, 0xD3, 0xF2,         // OUT (F2),01 sector 1
            0x3E, 0x80, 0xD3, 0xF0,         // OUT (F0),80 read sector
            0x21, 0x00, 0x41,               // LD HL,4100
            0x06, 0x00,                     // LD B,0
            0x0E, 0xF3,                     // LD C,F3
            0xED, 0xB2,                     // INIR
            0x3E, 0x77,                     // LD A,77
            0x32, 0x02, 0x40,               // LD (AfterInir),A
            0x18, 0xFE                      // JR $
        };
        var machine = new Cp500Machine(
            Cp500MachineProfile.Original,
            Rom(main, nmi: NmiHandler),
            images);

        for (int step = 0; step < 5000 && machine.Memory.Read(AfterInir) != 0x77; step++)
            machine.Step();

        Assert.Equal(0x77, machine.Memory.Read(AfterInir));
        Assert.Equal(1, machine.Memory.Read(NmiCounter));
        Assert.Equal(0x4200, machine.Cpu.HL);
        Assert.Equal(0, machine.Cpu.B);
    }
}
