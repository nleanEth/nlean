using Lean.Consensus;
using Lean.Consensus.Api;
using Lean.Metrics;
using Lean.Network;
using Lean.Node.Configuration;
using Lean.Validator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Lean.Node;

public sealed class NodeService : BackgroundService
{
    private readonly ILogger<NodeService> _logger;
    private readonly NodeOptions _options;
    private readonly INetworkService _networkService;
    private readonly IConsensusService _consensusService;
    private readonly IValidatorService _validatorService;
    private readonly IMetricsService _metricsService;
    private LeanApiServer? _apiServer;

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

        try
        {
            _logger.LogInformation("NodeService startup: starting metrics service.");
            await _metricsService.StartAsync(stoppingToken);
            LeanMetrics.SetNodeInfo(_options.NodeName ?? string.Empty, ResolveNodeVersion());
            LeanMetrics.SetIsAggregator(_options.Consensus.IsAggregator);
            LeanMetrics.SetAttestationCommitteeCount(_options.Consensus.AttestationCommitteeCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metrics service failed to start; continuing without metrics.");
        }
        _logger.LogInformation("NodeService startup: starting network service.");
        await _networkService.StartAsync(stoppingToken);
        _logger.LogInformation("NodeService startup: starting consensus service.");
        await _consensusService.StartAsync(stoppingToken);

        if (_options.Validator.Enabled)
        {
            if (_consensusService is ConsensusServiceV2 csv2)
                csv2.SetDutyTarget(_validatorService);
            _logger.LogInformation("NodeService startup: starting validator service.");
            await _validatorService.StartAsync(stoppingToken);
        }

        // Connect to bootstrap peers AFTER starting the validator service.
        // This is best-effort — the reconnect loop (started inside ConnectToPeersAsync)
        // handles ongoing connectivity regardless of initial connection results.
        // We must not let bootstrap failures prevent the node from starting:
        // consensus, validator, and the QUIC listener are already active and
        // peers can connect inbound while the reconnect loop retries outbound.
        try
        {
            _logger.LogInformation("NodeService startup: connecting to bootstrap peers.");
            await _networkService.ConnectToPeersAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Initial bootstrap connection failed; reconnect loop will retry.");
        }

        _logger.LogInformation("Lean node started. Network: {Network}", _options.Network);

        try
        {
            var csv2ForApi = _consensusService as ConsensusServiceV2;
            _apiServer = new LeanApiServer(
                $"http://+:{_options.ApiPort}/",
                () => csv2ForApi?.GetApiSnapshot() ?? new ApiSnapshot(0, "", 0, ""),
                () => csv2ForApi?.GetFinalizedStateSsz());
            await _apiServer.StartAsync(stoppingToken);
            _logger.LogInformation("LeanApiServer listening on port {Port}.", _options.ApiPort);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LeanApiServer failed to start; continuing without API.");
        }

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
        const int perServiceTimeoutMs = 10_000;
        if (_apiServer is not null)
        {
            try { await _apiServer.StopAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "LeanApiServer stop failed."); }
        }
        if (_consensusService is ConsensusServiceV2 csv2)
            csv2.SetDutyTarget(null);
        await StopServiceAsync(_validatorService.StopAsync, nameof(_validatorService), perServiceTimeoutMs);
        await StopServiceAsync(_consensusService.StopAsync, nameof(_consensusService), perServiceTimeoutMs);
        await StopServiceAsync(_networkService.StopAsync, nameof(_networkService), perServiceTimeoutMs);
        await StopServiceAsync(_metricsService.StopAsync, nameof(_metricsService), perServiceTimeoutMs);
        await base.StopAsync(cancellationToken);
    }

    private async Task StopServiceAsync(Func<CancellationToken, Task> stopFunc, string name, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await stopFunc(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{Service} stop timed out after {Timeout}ms, continuing shutdown.", name, timeoutMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Service} stop failed, continuing shutdown.", name);
        }
    }

    private static string ResolveNodeVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(NodeService).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
