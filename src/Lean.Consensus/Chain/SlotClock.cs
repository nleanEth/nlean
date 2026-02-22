namespace Lean.Consensus.Chain;

public sealed class SlotClock
{
    private readonly ulong _genesisTimeUnix;
    private readonly int _secondsPerSlot;
    private readonly int _intervalsPerSlot;
    private readonly long _msPerInterval;
    private readonly long _msPerSlot;
    private readonly ITimeSource _timeSource;

    public SlotClock(ulong genesisTimeUnix, int secondsPerSlot, int intervalsPerSlot, ITimeSource timeSource)
    {
        _genesisTimeUnix = genesisTimeUnix;
        _secondsPerSlot = secondsPerSlot;
        _intervalsPerSlot = intervalsPerSlot;
        _msPerSlot = secondsPerSlot * 1000L;
        _msPerInterval = _msPerSlot / intervalsPerSlot;
        _timeSource = timeSource;
    }

    public ulong CurrentSlot
    {
        get
        {
            var elapsedMs = ElapsedMs();
            if (elapsedMs < 0) return 0;
            return (ulong)elapsedMs / (ulong)_msPerSlot;
        }
    }

    public int CurrentInterval
    {
        get
        {
            var elapsedMs = ElapsedMs();
            if (elapsedMs < 0) return 0;
            var withinSlot = (ulong)elapsedMs % (ulong)_msPerSlot;
            return (int)(withinSlot / (ulong)_msPerInterval);
        }
    }

    public ulong TotalIntervals
    {
        get
        {
            var elapsedMs = ElapsedMs();
            if (elapsedMs < 0) return 0;
            return (ulong)elapsedMs / (ulong)_msPerInterval;
        }
    }

    public double SecondsUntilNextInterval
    {
        get
        {
            var elapsedMs = ElapsedMs();
            if (elapsedMs < 0) return (double)_msPerInterval / 1000.0;
            var intoInterval = (ulong)elapsedMs % (ulong)_msPerInterval;
            var remaining = (ulong)_msPerInterval - intoInterval;
            if (remaining == 0) remaining = (ulong)_msPerInterval;
            return (double)remaining / 1000.0;
        }
    }

    private long ElapsedMs()
    {
        var now = _timeSource.UtcNow;
        var genesis = DateTimeOffset.FromUnixTimeSeconds((long)_genesisTimeUnix);
        return (long)(now - genesis).TotalMilliseconds;
    }
}
