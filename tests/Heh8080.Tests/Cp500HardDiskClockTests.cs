using Heh8080.Core;
using Heh8080.Devices;

namespace Heh8080.Tests;

public sealed class Cp500HardDiskClockTests
{
    [Fact]
    public void ReadCommandReturnsSixBinaryBytesAfterBusyClears()
    {
        var first = new HardDiskTime(1, 2, 3, 12, 4, 5);
        var second = new HardDiskTime(9, 8, 7, 12, 29, 2);
        var clock = new FixedClock(first);
        var (controller, bus) = Board(clock, delayCycles: 15);

        bus.Out(Cp500HardDiskController.ExtraCommandPort, 0x11);
        Assert.Equal(Cp500HardDiskController.StatusBusy, (byte)(bus.In(Cp500HardDiskController.StatusCommandPort) & Cp500HardDiskController.StatusBusy));
        Assert.NotEqual(first.Seconds, bus.In(Cp500HardDiskController.ExtraDataPort));

        clock.Current = second;
        controller.AdvanceCycles(15);
        Assert.Equal(0, bus.In(Cp500HardDiskController.StatusCommandPort) & Cp500HardDiskController.StatusBusy);

        byte[] got = new byte[6];
        got[0] = bus.In(Cp500HardDiskController.ExtraDataPort);
        clock.Current = first;
        for (int i = 1; i < got.Length; i++)
            got[i] = bus.In(Cp500HardDiskController.ExtraDataPort);

        Assert.Equal(new byte[] { 9, 8, 7, 12, 29, 2 }, got);
        Assert.Equal(0, bus.In(Cp500HardDiskController.ExtraDataPort));
        Assert.Equal(0, bus.In(Cp500HardDiskController.StatusCommandPort) & Cp500HardDiskController.StatusDataRequest);
    }

    [Fact]
    public void SetCommandCommitsOnTheSixthByte()
    {
        var clock = new FixedClock(new HardDiskTime(0, 0, 0, 12, 1, 1));
        var (_, bus) = Board(clock, delayCycles: 0);
        bus.Out(Cp500HardDiskController.ExtraCommandPort, 0x02);
        bus.Out(Cp500HardDiskController.ExtraDataPort, 3);
        bus.Out(Cp500HardDiskController.ExtraDataPort, 2);
        bus.Out(Cp500HardDiskController.ExtraDataPort, 1);
        bus.Out(Cp500HardDiskController.ExtraDataPort, 12);
        bus.Out(Cp500HardDiskController.ExtraDataPort, 29);
        Assert.Equal(new HardDiskTime(0, 0, 0, 12, 1, 1), clock.Current);

        bus.Out(Cp500HardDiskController.ExtraDataPort, 2);
        Assert.Equal(new HardDiskTime(3, 2, 1, 12, 29, 2), clock.Current);
        Assert.Equal(0, bus.In(0xC5));

        bus.Out(Cp500HardDiskController.ExtraDataPort, 0x99);
        Assert.Equal(0x99, bus.In(Cp500HardDiskController.ExtraDataPort));
        Assert.Equal(new HardDiskTime(3, 2, 1, 12, 29, 2), clock.Current);

        var delayed = new FixedClock(new HardDiskTime(0, 0, 0, 12, 1, 1));
        var (delayedController, delayedBus) = Board(delayed, delayCycles: 4);
        delayedBus.Out(Cp500HardDiskController.ExtraCommandPort, 0x02);
        delayedBus.Out(Cp500HardDiskController.ExtraDataPort, 50);
        delayedController.AdvanceCycles(4);
        delayedBus.Out(Cp500HardDiskController.ExtraDataPort, 4);
        delayedBus.Out(Cp500HardDiskController.ExtraDataPort, 5);
        delayedBus.Out(Cp500HardDiskController.ExtraDataPort, 6);
        delayedBus.Out(Cp500HardDiskController.ExtraDataPort, 12);
        delayedBus.Out(Cp500HardDiskController.ExtraDataPort, 7);
        delayedBus.Out(Cp500HardDiskController.ExtraDataPort, 8);
        Assert.Equal(new HardDiskTime(4, 5, 6, 12, 7, 8), delayed.Current);
    }

    [Fact]
    public void PartialSetAndAnImpossibleDateLeaveTheClock()
    {
        var original = new HardDiskTime(3, 2, 1, 12, 29, 2);
        var clock = new FixedClock(original);
        var (_, bus) = Board(clock, delayCycles: 0);

        bus.Out(Cp500HardDiskController.ExtraCommandPort, 0x02);
        bus.Out(Cp500HardDiskController.ExtraDataPort, 1);
        bus.Out(Cp500HardDiskController.ExtraDataPort, 1);
        bus.Out(Cp500HardDiskController.StatusCommandPort, 0x10);
        Assert.Equal(original, clock.Current);

        WriteClock(bus, 0, 0, 0, 12, 31, 2);
        Assert.Equal(original, clock.Current);
        Assert.Equal(Cp500HardDiskController.ErrorAborted, bus.In(0xC5));
        Assert.Equal(0, bus.In(0xC9));

        WriteClock(bus, 60, 0, 0, 12, 1, 1);
        Assert.Equal(original, clock.Current);
        Assert.Equal(Cp500HardDiskController.ErrorAborted, bus.In(0xC5));

        WriteClock(bus, 0, 0, 0, 11, 29, 2);
        Assert.Equal(original, clock.Current);
    }

    [Fact]
    public void OffsetClockUsesTheSuppliedInstant()
    {
        var now = new DateTime(2012, 2, 29, 1, 2, 3, 900, DateTimeKind.Local);
        var clock = new OffsetClock(() => now);

        Assert.Equal(new HardDiskTime(3, 2, 1, 12, 29, 2), clock.Read());
        clock.Set(new HardDiskTime(0, 30, 4, 12, 29, 2));
        now = now.AddHours(2);
        Assert.Equal(new HardDiskTime(0, 30, 6, 12, 29, 2), clock.Read());

        var impossible = new HardDiskTime(0, 0, 0, 12, 31, 2);
        Assert.False(impossible.TryToLocalDateTime(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Set(impossible));
        Assert.Equal(new HardDiskTime(0, 30, 6, 12, 29, 2), clock.Read());
    }

    private static void WriteClock(IoBus bus, byte seconds, byte minutes, byte hours, byte year, byte day, byte month)
    {
        bus.Out(Cp500HardDiskController.ExtraCommandPort, 0x02);
        bus.Out(Cp500HardDiskController.ExtraDataPort, seconds);
        bus.Out(Cp500HardDiskController.ExtraDataPort, minutes);
        bus.Out(Cp500HardDiskController.ExtraDataPort, hours);
        bus.Out(Cp500HardDiskController.ExtraDataPort, year);
        bus.Out(Cp500HardDiskController.ExtraDataPort, day);
        bus.Out(Cp500HardDiskController.ExtraDataPort, month);
    }

    private static (Cp500HardDiskController Controller, IoBus Bus) Board(IClock clock, long delayCycles)
    {
        var controller = new Cp500HardDiskController(new HardDiskSlot(), clock, new HardDiskTiming(delayCycles));
        var bus = new IoBus();
        controller.Register(bus);
        return (controller, bus);
    }

    private sealed class FixedClock(HardDiskTime time) : IClock
    {
        public HardDiskTime Current { get; set; } = time;

        public HardDiskTime Read() => Current;

        public void Set(HardDiskTime time) => Current = time;
    }
}
