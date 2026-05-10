using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lean.Consensus.Chain;

public sealed class ChainService
{
    private readonly SlotClock _clock;
    private readonly ITickTarget _target;
    private readonly int _intervalsPerSlot;
    private readonly ILogger<ChainService> _logger;
    private ulong _lastProcessedTotalInterval;
    private bool _initialized;

    public ChainService(SlotClock clock, ITickTarget target, int intervalsPerSlot, ILogger<ChainService>? logger = null)
    {
        _clock = clock;
        _target = target;
        _intervalsPerSlot = intervalsPerSlot;
        _logger = logger ?? NullLogger<ChainService>.Instance;
    }

    public void TickToCurrent()
    {
        var currentTotal = _clock.TotalIntervals;

        if (!_initialized)
        {
            _initialized = true;
            // Skip historical replay; fire only the current interval.
            _lastProcessedTotalInterval = currentTotal;

            if (currentTotal == 0)
                return;

            var slot = currentTotal / (ulong)_intervalsPerSlot;
            var interval = (int)(currentTotal % (ulong)_intervalsPerSlot);
            _logger.LogInformation(
                "ChainService initialized at interval {CurrentTotal} (slot {Slot}, interval {Interval})",
                currentTotal, slot, interval);
            _target.OnTick(slot, interval);
            _lastProcessedTotalInterval = currentTotal + 1;
            return;
        }

        // OnTick(slot, N) fires when currentTotal == N (interval N start).
        while (_lastProcessedTotalInterval <= currentTotal)
        {
            var slot = _lastProcessedTotalInterval / (ulong)_intervalsPerSlot;
            var interval = (int)(_lastProcessedTotalInterval % (ulong)_intervalsPerSlot);
            _target.OnTick(slot, interval);
            _lastProcessedTotalInterval++;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("ChainService tick loop started. IntervalsPerSlot: {IntervalsPerSlot}", _intervalsPerSlot);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TickToCurrent();
                var delay = _clock.SecondsUntilNextInterval;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(delay, 0.01)), ct);
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("ChainService tick loop stopped.");
    }
}
