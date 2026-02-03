using Lean.Consensus;
using Lean.Metrics;
using Lean.Network;
using Lean.Node.Configuration;
using Lean.Validator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lean.Node;

public sealed class NodeService : BackgroundService
{
    private readonly ILogger<NodeService> _logger;
    private readonly NodeOptions _options;
    private readonly INetworkService _networkService;
    private readonly IConsensusService _consensusService;
    private readonly IValidatorService _validatorService;
    private readonly IMetricsService _metricsService;

    public NodeService(
        ILogger<NodeService> logger,
        NodeOptions options,
        INetworkService networkService,
        IConsensusService consensusService,
        IValidatorService validatorService,
        IMetricsService metricsService)
    {
        _logger = logger;
        _options = options;
        _networkService = networkService;
        _consensusService = consensusService;
        _validatorService = validatorService;
        _metricsService = metricsService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ValidatorConfigPath))
        {
            var validatorConfig = ValidatorConfig.Load(_options.ValidatorConfigPath);
            var nodeConfig = validatorConfig.FindNode(_options.NodeName);

            if (nodeConfig is null)
            {
                _logger.LogWarning("Node name {NodeName} not found in validator-config.yaml; running without node-specific config.", _options.NodeName);
            }
            else
            {
                _logger.LogInformation("Loaded validator node config for {NodeName}.", nodeConfig.Name);
            }
        }

        await _metricsService.StartAsync(stoppingToken);
        await _networkService.StartAsync(stoppingToken);
        await _consensusService.StartAsync(stoppingToken);

        if (_options.Validator.Enabled)
        {
            await _validatorService.StartAsync(stoppingToken);
        }

        _logger.LogInformation("Lean node started. Network: {Network}", _options.Network);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Ignore shutdown
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _validatorService.StopAsync(cancellationToken);
        await _consensusService.StopAsync(cancellationToken);
        await _networkService.StopAsync(cancellationToken);
        await _metricsService.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
