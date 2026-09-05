namespace Ocrx.Engine.Tests;

internal sealed class MonotonicTestTimeProvider : TimeProvider
{
    private int _timestampReads;
    private int _timerCreations;

    public int TimestampReads => Volatile.Read(ref _timestampReads);

    public int TimerCreations => Volatile.Read(ref _timerCreations);

    public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

    public override DateTimeOffset GetUtcNow()
        => throw new InvalidOperationException("Realtime pacing must not consult wall-clock time.");

    public override long GetTimestamp()
    {
        Interlocked.Increment(ref _timestampReads);
        return TimeProvider.System.GetTimestamp();
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        Interlocked.Increment(ref _timerCreations);
        return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }
}
