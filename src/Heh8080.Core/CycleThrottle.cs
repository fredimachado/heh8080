namespace Heh8080.Core;

/// <summary>
/// Converts emulated instruction cycles into host delays without blocking the caller.
/// </summary>
public sealed class CycleThrottle
{
    private readonly long _cyclesPerSecond;
    private readonly TimeProvider _time;
    private long _baseline;
    private long _emulatedCycles;
    private double _speedFactor = 1;

    /// <summary>Creates a throttle for a positive CPU frequency, timed by <paramref name="time"/> or the system clock.</summary>
    public CycleThrottle(long cyclesPerSecond, TimeProvider? time = null)
    {
        if (cyclesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(cyclesPerSecond));

        _cyclesPerSecond = cyclesPerSecond;
        _time = time ?? TimeProvider.System;
        _baseline = _time.GetTimestamp();
    }

    /// <summary>
    /// Multiple of the configured frequency to pace at. <see cref="double.PositiveInfinity"/> never delays.
    /// Setting it starts a fresh baseline, so time run at the previous speed is neither caught up nor waited out.
    /// </summary>
    public double SpeedFactor
    {
        get => _speedFactor;
        set
        {
            if (!(value > 0))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The speed factor must be positive.");

            _speedFactor = value;
            Reset();
        }
    }

    /// <summary>
    /// Accounts for executed cycles and returns the delay needed to stay at the configured frequency.
    /// Callers can await this value with their platform's scheduler; no thread is slept here.
    /// </summary>
    public TimeSpan GetDelay(int cycles)
    {
        if (cycles < 0)
            throw new ArgumentOutOfRangeException(nameof(cycles));

        _emulatedCycles += cycles;
        double targetSeconds = _emulatedCycles / (_cyclesPerSecond * _speedFactor);
        TimeSpan remaining = TimeSpan.FromSeconds(targetSeconds) - _time.GetElapsedTime(_baseline);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Starts a fresh host-time and emulated-cycle baseline.</summary>
    public void Reset()
    {
        _emulatedCycles = 0;
        _baseline = _time.GetTimestamp();
    }
}
