using System.Text.Json;
using Lean.Metrics;
using Lean.Network;
using Lean.Storage;

namespace Lean.Node.Configuration;

public sealed class NodeOptions
{
    public string DataDir { get; set; } = "data";
    public string Network { get; set; } = "devnet2";
    public string? Libp2pConfigPath { get; set; }
    public string? ValidatorConfigPath { get; set; }
    public string? NodeName { get; set; }
    public Libp2pConfig Libp2p { get; set; } = new();
    public MetricsConfig Metrics { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public ValidatorRuntimeConfig Validator { get; set; } = new();

    public static NodeOptions Load(NodeOptionsOverrides overrides)
    {
        var options = new NodeOptions();
        var configPath = overrides.ConfigPath ?? "config/node-config.json";

        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            var fromFile = JsonSerializer.Deserialize<NodeOptions>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (fromFile is not null)
            {
                options = fromFile;
            }
        }

        options.Libp2p ??= new Libp2pConfig();
        options.Metrics ??= new MetricsConfig();
        options.Storage ??= new StorageConfig();
        options.Logging ??= new LoggingConfig();
        options.Validator ??= new ValidatorRuntimeConfig();

        if (!string.IsNullOrWhiteSpace(overrides.DataDir))
        {
            options.DataDir = overrides.DataDir;
        }

        if (!string.IsNullOrWhiteSpace(overrides.Network))
        {
            options.Network = overrides.Network;
        }

        if (overrides.MetricsEnabled.HasValue)
        {
            options.Metrics.Enabled = overrides.MetricsEnabled.Value;
        }

        if (!string.IsNullOrWhiteSpace(overrides.LogLevel))
        {
            options.Logging.Level = overrides.LogLevel;
        }

        if (!string.IsNullOrWhiteSpace(overrides.ValidatorConfigPath))
        {
            options.ValidatorConfigPath = overrides.ValidatorConfigPath;
        }

        if (!string.IsNullOrWhiteSpace(overrides.NodeName))
        {
            options.NodeName = overrides.NodeName;
        }

        if (!string.IsNullOrWhiteSpace(overrides.Libp2pConfig))
        {
            options.Libp2pConfigPath = overrides.Libp2pConfig;
        }

        if (string.IsNullOrWhiteSpace(options.Storage.DataDir) || options.Storage.DataDir == "data")
        {
            options.Storage.DataDir = options.DataDir;
        }

        return options;
    }
}

public sealed class LoggingConfig
{
    public string Level { get; set; } = "Information";
}

public sealed class ValidatorRuntimeConfig
{
    public bool Enabled { get; set; } = true;
    public string? KeystorePath { get; set; }
}
