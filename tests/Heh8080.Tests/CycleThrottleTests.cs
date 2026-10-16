using Heh8080.Core;

namespace Heh8080.Tests;

public sealed class CycleThrottleTests
{
    private const long Hertz = 1_000_000;

    private readonly ManualTimeProvider _time = new();
    private readonly CycleThrottle _throttle;

    public CycleThrottleTests() => _throttle = new CycleThrottle(Hertz, _time);

    [Fact]
    public void DelayIsTheEmulatedTimeNotYetElapsedOnTheHost()
    {
        _time.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(TimeSpan.FromMilliseconds(700), _throttle.GetDelay((int)Hertz));
    }

    [Fact]
    public void ARunBehindTheHostIsNotDelayed()
    {
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.Zero, _throttle.GetDelay((int)Hertz));
    }

    [Theory]
    [InlineData(2, 500)]
    [InlineData(4, 250)]
    public void ASpeedFactorPacesAtThatMultipleOfTheClock(double factor, int milliseconds)
    {
        _throttle.SpeedFactor = factor;

        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), _throttle.GetDelay((int)Hertz));
    }

    [Fact]
    public void InfiniteSpeedNeverDelays()
    {
        _throttle.SpeedFactor = double.PositiveInfinity;

        Assert.Equal(TimeSpan.Zero, _throttle.GetDelay(int.MaxValue));
    }

    [Fact]
    public void ChangingTheSpeedForgetsTheTimeRunAtThePreviousSpeed()
    {
        _throttle.SpeedFactor = double.PositiveInfinity;
        for (int second = 0; second < 60; second++)
            _throttle.GetDelay((int)Hertz);
        _time.Advance(TimeSpan.FromSeconds(1));

        _throttle.SpeedFactor = 1;

        Assert.Equal(TimeSpan.FromSeconds(1), _throttle.GetDelay((int)Hertz));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void SpeedFactorMustBePositive(double factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _throttle.SpeedFactor = factor);
        Assert.Equal(1, _throttle.SpeedFactor);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
