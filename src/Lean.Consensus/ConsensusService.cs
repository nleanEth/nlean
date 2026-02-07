using Microsoft.Extensions.Logging;

namespace Lean.Consensus;

public sealed class ConsensusService : IConsensusService
{
    private readonly ILogger<ConsensusService> _logger;

    public ConsensusService(ILogger<ConsensusService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consensus service started - stub. TODO: wire leanSpec state transition.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consensus service stopped.");
        return Task.CompletedTask;
    }
}
