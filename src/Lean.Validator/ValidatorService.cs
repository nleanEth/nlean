using System.Diagnostics.Metrics;
using Lean.Consensus;
using Lean.Crypto;
using Microsoft.Extensions.Logging;

namespace Lean.Validator;

public sealed class ValidatorService : IValidatorService
{
    private static readonly Meter ValidatorMeter = new("Lean.Validator");
    private static readonly Counter<long> DutyRunsTotal = ValidatorMeter.CreateCounter<long>(
        "lean_validator_duty_runs_total",
        description: "Total number of validator duty loop ticks executed.");

    private readonly ILogger<ValidatorService> _logger;
    private readonly IConsensusService _consensusService;
    private readonly ConsensusConfig _consensusConfig;
    private readonly ILeanSig _leanSig;
    private readonly ILeanMultiSig _leanMultiSig;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _dutyLoopCts;
    private Task? _dutyLoopTask;
    private int _started;

    public ValidatorService(
        ILogger<ValidatorService> logger,
        IConsensusService consensusService,
        ConsensusConfig consensusConfig,
        ILeanSig leanSig,
        ILeanMultiSig leanMultiSig)
    {
        _logger = logger;
        _consensusService = consensusService;
        _consensusConfig = consensusConfig;
        _leanSig = leanSig;
        _leanMultiSig = leanMultiSig;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            // Initialize native proving/verifying contexts once per active lifecycle.
            _leanMultiSig.SetupProver();
            _leanMultiSig.SetupVerifier();

            lock (_lifecycleLock)
            {
                _dutyLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _dutyLoopTask = RunDutyLoopAsync(_dutyLoopCts.Token);
            }
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }

        _logger.LogInformation(
            "Validator service started. SecondsPerSlot: {SecondsPerSlot}",
            Math.Max(1, _consensusConfig.SecondsPerSlot));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        CancellationTokenSource? dutyLoopCts;
        Task? dutyLoopTask;
        lock (_lifecycleLock)
        {
            dutyLoopCts = _dutyLoopCts;
            dutyLoopTask = _dutyLoopTask;
            _dutyLoopCts = null;
            _dutyLoopTask = null;
        }

        if (dutyLoopCts is not null)
        {
            dutyLoopCts.Cancel();
            dutyLoopCts.Dispose();
        }

        if (dutyLoopTask is not null)
        {
            try
            {
                await dutyLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Shutdown path.
            }
        }

        _logger.LogInformation("Validator service stopped.");
    }

    private async Task RunDutyLoopAsync(CancellationToken cancellationToken)
    {
        var secondsPerSlot = Math.Max(1, _consensusConfig.SecondsPerSlot);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(secondsPerSlot));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                ExecuteNoOpDuty();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
    }

    private void ExecuteNoOpDuty()
    {
        // Placeholder baseline duty: avoid signing until duty/message formats are specified.
        var slot = _consensusService.CurrentSlot;
        DutyRunsTotal.Add(1);
        _logger.LogDebug("Executed validator duty tick for slot {Slot}.", slot);
    }
}
