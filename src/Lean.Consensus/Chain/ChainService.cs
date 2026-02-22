namespace Lean.Consensus.Chain;

public sealed class ChainService
{
    private readonly SlotClock _clock;
    private readonly ITickTarget _target;
    private readonly int _intervalsPerSlot;
    private ulong _lastProcessedTotalInterval;
    private bool _initialized;

    public ChainService(SlotClock clock, ITickTarget target, int intervalsPerSlot)
    {
        _clock = clock;
        _target = target;
        _intervalsPerSlot = intervalsPerSlot;
    }

    public void TickToCurrent()
    {
        var currentTotal = _clock.TotalIntervals;
        if (currentTotal == 0 && !_initialized)
        {
            _initialized = true;
            return;
        }

        if (!_initialized)
        {
            _initialized = true;
            // Emit all intervals from 0 to currentTotal-1
            for (ulong i = 0; i < currentTotal; i++)
            {
                var slot = i / (ulong)_intervalsPerSlot;
                var interval = (int)(i % (ulong)_intervalsPerSlot);
                _target.OnTick(slot, interval);
            }

            _lastProcessedTotalInterval = currentTotal;
            return;
        }

        while (_lastProcessedTotalInterval < currentTotal)
        {
            var slot = _lastProcessedTotalInterval / (ulong)_intervalsPerSlot;
            var interval = (int)(_lastProcessedTotalInterval % (ulong)_intervalsPerSlot);
            _target.OnTick(slot, interval);
            _lastProcessedTotalInterval++;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TickToCurrent();
            var delay = _clock.SecondsUntilNextInterval;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(delay, 0.01)), ct);
        }
    }
}
