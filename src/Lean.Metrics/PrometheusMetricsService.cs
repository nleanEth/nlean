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

        var listenHost = NormalizeMetricServerHost(_config.Host);
        _server = new MetricServer(listenHost, _config.Port);
        _server.Start();
        _logger.LogInformation(
            "Metrics server listening on {Host}:{Port} (configured host: {ConfiguredHost})",
            listenHost,
            _config.Port,
            _config.Host);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Stop();
        _server = null;
        return Task.CompletedTask;
    }

    private static string NormalizeMetricServerHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "+";
        }

        var trimmed = host.Trim();
        return trimmed switch
        {
            "0.0.0.0" => "+",
            "::" => "+",
            "[::]" => "+",
            _ => trimmed,
        };
    }
}
