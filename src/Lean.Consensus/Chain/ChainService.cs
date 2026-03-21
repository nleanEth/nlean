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
        if (currentTotal == 0 && !_initialized)
        {
            _initialized = true;
            return;
        }

        if (!_initialized)
        {
            _initialized = true;
            var catchUpSlots = currentTotal / (ulong)_intervalsPerSlot;
            _logger.LogInformation(
                "ChainService catching up from interval 0 to {CurrentTotal} ({SlotCount} slots)",
                currentTotal, catchUpSlots);
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
