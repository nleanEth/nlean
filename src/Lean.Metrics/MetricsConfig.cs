namespace Lean.Metrics;

public sealed class MetricsConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8008;
}
