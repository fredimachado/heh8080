using Heh8080.Core;

namespace Heh8080.Devices;

/// <summary>
/// Three BCD clock registers mapped at 4040h-4042h: seconds, minutes, and hours.
/// Writes adjust an emulated offset from the injected host clock.
/// </summary>
public sealed class Cp500RtcDevice : MemoryRegion
{
    /// <summary>First real-time-clock address.</summary>
    public const ushort ClockStartAddress = 0x4040;

    /// <summary>Number of clock registers.</summary>
    public const int RegisterCount = 3;

    private readonly TimeProvider _timeProvider;
    private TimeSpan _offset;

    /// <summary>Creates an RTC backed by <see cref="TimeProvider.System"/> unless supplied otherwise.</summary>
    public Cp500RtcDevice(TimeProvider? timeProvider = null)
        : base(ClockStartAddress, RegisterCount)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override byte Read(ushort address)
    {
        DateTime now = GetCurrentTime();
        return GetRegisterOffset(address) switch
        {
            0 => ToBcd(now.Second),
            1 => ToBcd(now.Minute),
            2 => ToBcd(now.Hour),
            _ => throw new ArgumentOutOfRangeException(nameof(address))
        };
    }

    public override void Write(ushort address, byte value)
    {
        if (!TryFromBcd(value, out int component))
            return;

        DateTime current = GetCurrentTime();
        DateTime requested = GetRegisterOffset(address) switch
        {
            0 when component < 60 => new DateTime(
                current.Year, current.Month, current.Day, current.Hour, current.Minute, component, current.Kind),
            1 when component < 60 => new DateTime(
                current.Year, current.Month, current.Day, current.Hour, component, current.Second, current.Kind),
            2 when component < 24 => new DateTime(
                current.Year, current.Month, current.Day, component, current.Minute, current.Second, current.Kind),
            _ => current
        };

        _offset += requested - current;
    }

    /// <summary>Sets all clock registers at once.</summary>
    public void SetTime(DateTimeOffset time)
    {
        _offset = time.DateTime - _timeProvider.GetLocalNow().DateTime;
    }

    private DateTime GetCurrentTime() => _timeProvider.GetLocalNow().Add(_offset).DateTime;

    private int GetRegisterOffset(ushort address) => address - ClockStartAddress;

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    private static bool TryFromBcd(byte value, out int result)
    {
        int tens = value >> 4;
        int ones = value & 0x0F;
        result = tens * 10 + ones;
        return tens < 10 && ones < 10;
    }
}
