using Microsoft.Extensions.Logging;
using Prometheus;

namespace Lean.Metrics;

public sealed class PrometheusMetricsService : IMetricsService
{
    private readonly MetricsConfig _config;
    private readonly ILogger<PrometheusMetricsService> _logger;
    private MetricServer? _server;

    public PrometheusMetricsService(MetricsConfig config, ILogger<PrometheusMetricsService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            return Task.CompletedTask;
        }

        _server = new MetricServer(_config.Host, _config.Port);
        _server.Start();
        _logger.LogInformation("Metrics server listening on {Host}:{Port}", _config.Host, _config.Port);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Stop();
        _server = null;
        return Task.CompletedTask;
    }
}
