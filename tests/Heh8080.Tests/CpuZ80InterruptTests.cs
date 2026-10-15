using Heh8080.Core;

namespace Heh8080.Tests;

public sealed class CpuZ80InterruptTests
{
    private const byte Nop = 0x00;
    private const byte Ei = 0xFB;
    private const byte Di = 0xF3;
    private const byte Halt = 0x76;

    private readonly Memory _memory = new();
    private readonly ScriptedIoBus _io = new();
    private readonly CpuZ80 _cpu;

    public CpuZ80InterruptTests()
    {
        _cpu = new CpuZ80(_memory, _io);
        _cpu.InterruptMode = 1;
        _cpu.SP = 0x8000;
    }

    private void Load(ushort address, params byte[] bytes)
    {
        for (int index = 0; index < bytes.Length; index++)
            _memory.Write((ushort)(address + index), bytes[index]);
    }

    [Fact]
    public void MaskableRequestRaisedWhileDisabledIsServicedAfterEi()
    {
        Load(0x0000, Ei, Nop, Nop);
        _cpu.Interrupt(7);

        _cpu.Step();
        Assert.Equal(0x0001, _cpu.PC);

        _cpu.Step();
        Assert.Equal(0x0002, _cpu.PC);

        _cpu.Step();
        Assert.Equal(0x0038, _cpu.PC);
        Assert.False(_cpu.IFF1);
    }

    [Fact]
    public void InterruptIsNotAcceptedInTheInstructionAfterEi()
    {
        Load(0x0000, Ei, Di);
        _cpu.SetInterruptLine(true);

        _cpu.Step();
        _cpu.Step();

        Assert.Equal(0x0002, _cpu.PC);
        Assert.False(_cpu.IFF1);
    }

    [Fact]
    public void LevelLineKeepsInterruptingUntilDeasserted()
    {
        Load(0x0038, Ei, Nop);
        _cpu.IFF1 = _cpu.IFF2 = true;
        _cpu.SetInterruptLine(true);

        _cpu.Step();
        Assert.Equal(0x0038, _cpu.PC);

        _cpu.Step();
        Assert.Equal(0x0039, _cpu.PC);

        _cpu.Step();
        Assert.Equal(0x003A, _cpu.PC);

        _cpu.Step();
        Assert.Equal(0x0038, _cpu.PC);

        _cpu.SetInterruptLine(false);
        _cpu.Step();
        _cpu.Step();
        _cpu.Step();
        Assert.Equal(0x003B, _cpu.PC);
    }

    [Fact]
    public void AcceptingAnInterruptCostsTheAcknowledgeCycles()
    {
        _cpu.IFF1 = _cpu.IFF2 = true;
        _cpu.SetInterruptLine(true);

        int mode1 = _cpu.Step();

        _cpu.InterruptMode = 2;
        _cpu.I = 0x10;
        Load(0x10FE, 0x34, 0x12);
        _cpu.IFF1 = _cpu.IFF2 = true;

        int mode2 = _cpu.Step();

        Assert.Equal(13, mode1);
        Assert.Equal(19, mode2);
        Assert.Equal(0x1234, _cpu.PC);
    }

    [Fact]
    public void Mode0ExecutesTheRstOpcodePresentedOnTheDataBus()
    {
        _cpu.InterruptMode = 0;
        _cpu.IFF1 = _cpu.IFF2 = true;
        _cpu.PC = 0x1234;
        _cpu.SetInterruptLine(true, 0xD7);

        _cpu.Step();

        Assert.Equal(0x0010, _cpu.PC);
        Assert.Equal(0x1234, ReadStackWord());
    }

    [Fact]
    public void InterruptWakesHaltAndReturnsAfterTheHalt()
    {
        Load(0x0000, Halt);
        _cpu.IFF1 = _cpu.IFF2 = true;
        _cpu.Step();
        Assert.True(_cpu.Halted);

        _cpu.SetInterruptLine(true);
        _cpu.Step();

        Assert.False(_cpu.Halted);
        Assert.Equal(0x0038, _cpu.PC);
        Assert.Equal(0x0001, ReadStackWord());
    }

    [Fact]
    public void NmiWakesHaltEvenWithInterruptsDisabled()
    {
        Load(0x0000, Halt);
        _cpu.Step();

        _cpu.NonMaskableInterrupt();
        _cpu.Step();

        Assert.False(_cpu.Halted);
        Assert.Equal(0x0066, _cpu.PC);
    }

    [Fact]
    public void NmiTakesPriorityAndPreservesTheEnableStateInIff2()
    {
        _cpu.IFF1 = _cpu.IFF2 = true;
        _cpu.SetInterruptLine(true);
        _cpu.NonMaskableInterrupt();

        int cycles = _cpu.Step();

        Assert.Equal(0x0066, _cpu.PC);
        Assert.Equal(11, cycles);
        Assert.False(_cpu.IFF1);
        Assert.True(_cpu.IFF2);
    }

    [Fact]
    public void NmiRaisedInsideAnInstructionIsTakenAtTheNextBoundary()
    {
        const byte Inir = 0xB2;
        _io.OnIn = count =>
        {
            if (count == 2)
                _cpu.NonMaskableInterrupt();
            return 0x55;
        };
        Load(0x0000, 0xED, Inir);
        _cpu.HL = 0x4000;
        _cpu.B = 3;
        _cpu.C = 0x10;

        _cpu.Step();
        _cpu.Step();

        Assert.Equal(0x0000, _cpu.PC);

        _cpu.Step();

        Assert.Equal(0x0066, _cpu.PC);
        Assert.Equal(0x0000, ReadStackWord());
        Assert.Equal(1, _cpu.B);
    }

    [Fact]
    public void WaitLineBlocksInterruptAcceptance()
    {
        _cpu.IFF1 = _cpu.IFF2 = true;
        _cpu.SetInterruptLine(true);
        _cpu.NonMaskableInterrupt();
        _cpu.WaitLine = true;

        _cpu.Step();
        Assert.Equal(0x0000, _cpu.PC);

        _cpu.WaitLine = false;
        _cpu.Step();
        Assert.Equal(0x0066, _cpu.PC);
    }

    [Fact]
    public void ResetClearsPendingRequests()
    {
        _cpu.IFF1 = _cpu.IFF2 = true;
        _cpu.SetInterruptLine(true);
        _cpu.NonMaskableInterrupt();
        _cpu.Interrupt(7);

        _cpu.Reset();
        _cpu.IFF1 = _cpu.IFF2 = true;
        _cpu.Step();

        Assert.Equal(0x0001, _cpu.PC);
    }

    private ushort ReadStackWord() =>
        (ushort)(_memory.Read(_cpu.SP) | (_memory.Read((ushort)(_cpu.SP + 1)) << 8));

    private sealed class ScriptedIoBus : IIoBus
    {
        private int _inCount;

        public Func<int, byte> OnIn { get; set; } = _ => 0xFF;

        public byte In(byte port) => OnIn(++_inCount);

        public void Out(byte port, byte value)
        {
        }
    }
}
