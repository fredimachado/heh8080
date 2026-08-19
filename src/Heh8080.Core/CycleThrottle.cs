using System.Diagnostics;

namespace Heh8080.Core;

/// <summary>
/// Converts emulated instruction cycles into host delays without blocking the caller.
/// </summary>
public sealed class CycleThrottle
{
    private readonly long _cyclesPerSecond;
    private readonly Stopwatch _clock = new();
    private long _emulatedCycles;

    /// <summary>Creates a throttle for a positive CPU frequency.</summary>
    public CycleThrottle(long cyclesPerSecond)
    {
        if (cyclesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(cyclesPerSecond));

        _cyclesPerSecond = cyclesPerSecond;
        _clock.Start();
    }

    /// <summary>Configured processor frequency.</summary>
    public long CyclesPerSecond => _cyclesPerSecond;

    /// <summary>
    /// Accounts for executed cycles and returns the delay needed to stay at the configured frequency.
    /// Callers can await this value with their platform's scheduler; no thread is slept here.
    /// </summary>
    public TimeSpan GetDelay(int cycles)
    {
        if (cycles < 0)
            throw new ArgumentOutOfRangeException(nameof(cycles));

        _emulatedCycles += cycles;
        double targetSeconds = (double)_emulatedCycles / _cyclesPerSecond;
        TimeSpan remaining = TimeSpan.FromSeconds(targetSeconds) - _clock.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Starts a fresh host-time and emulated-cycle baseline.</summary>
    public void Reset()
    {
        _emulatedCycles = 0;
        _clock.Restart();
    }
}
