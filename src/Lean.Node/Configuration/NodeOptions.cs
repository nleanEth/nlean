using System.Text.Json;
using Lean.Consensus;
using Lean.Metrics;
using Lean.Network;
using Lean.Storage;

namespace Lean.Node.Configuration;

public sealed class NodeOptions
{
    public string DataDir { get; set; } = "data";
    public string ForkDigest { get; set; } = GossipTopics.DefaultForkDigest;
    public string? ValidatorConfigPath { get; set; }
    public string? NodeName { get; set; }
    public Libp2pConfig Libp2p { get; set; } = new();
    public ConsensusConfig Consensus { get; set; } = new();
    public MetricsConfig Metrics { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public ValidatorRuntimeConfig Validator { get; set; } = new();
    public int ApiPort { get; set; } = 5052;
    public string? CheckpointSyncUrl { get; set; }
    public string? HashSigKeyDir { get; set; }
    public string? AnnotatedValidatorsPath { get; set; }

    public static NodeOptions Load(NodeOptionsOverrides overrides)
    {
        var options = new NodeOptions();
        var configPath = overrides.ConfigPath;

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
        options.Consensus ??= new ConsensusConfig();
        options.Metrics ??= new MetricsConfig();
        options.Storage ??= new StorageConfig();
        options.Logging ??= new LoggingConfig();
        options.Validator ??= new ValidatorRuntimeConfig();

        if (!string.IsNullOrWhiteSpace(overrides.DataDir))
        {
            options.DataDir = overrides.DataDir;
        }

        if (!string.IsNullOrWhiteSpace(overrides.ForkDigest))
        {
            options.ForkDigest = overrides.ForkDigest;
        }

        if (overrides.MetricsEnabled.HasValue)
        {
            options.Metrics.Enabled = overrides.MetricsEnabled.Value;
        }

        if (!string.IsNullOrWhiteSpace(overrides.LogLevel))
        {
            options.Logging.Level = overrides.LogLevel;
        }

        if (!string.IsNullOrWhiteSpace(overrides.NodeName))
        {
            options.NodeName = overrides.NodeName;
        }

        if (string.IsNullOrWhiteSpace(options.Storage.DataDir) || options.Storage.DataDir == "data")
        {
            options.Storage.DataDir = options.DataDir;
        }

        if (!string.IsNullOrWhiteSpace(overrides.CheckpointSyncUrl))
        {
            options.CheckpointSyncUrl = overrides.CheckpointSyncUrl;
        }

        if (!string.IsNullOrWhiteSpace(overrides.NodeKeyPath))
        {
            options.Libp2p.PrivateKeyPath = overrides.NodeKeyPath;
        }

        if (overrides.SocketPort.HasValue)
        {
            options.Libp2p.ListenAddresses = new List<string>
            {
                $"/ip4/0.0.0.0/udp/{overrides.SocketPort.Value}/quic-v1"
            };
        }

        if (overrides.MetricsPort.HasValue)
        {
            options.Metrics.Port = overrides.MetricsPort.Value;
        }

        if (!string.IsNullOrWhiteSpace(overrides.MetricsAddress))
        {
            options.Metrics.Host = overrides.MetricsAddress;
        }

        if (overrides.IsAggregator)
        {
            options.Validator.PublishAggregates = true;
        }

        if (overrides.AttestationCommitteeCount.HasValue)
        {
            options.Consensus.AttestationCommitteeCount = overrides.AttestationCommitteeCount.Value;
        }

        if (overrides.ApiPort.HasValue)
        {
            options.ApiPort = overrides.ApiPort.Value;
        }

        // --custom-network-config-dir is the single-flag config entry point
        // (matching ethlambda / gean / zeam). Everything inside is discovered
        // by name: config.yaml, nodes.yaml, annotated_validators.yaml,
        // hash-sig-keys/, and <node>.key.
        if (!string.IsNullOrWhiteSpace(overrides.CustomNetworkConfigDir))
        {
            var dir = overrides.CustomNetworkConfigDir;

            options.AnnotatedValidatorsPath = Path.Combine(dir, "annotated_validators.yaml");
            options.HashSigKeyDir = Path.Combine(dir, "hash-sig-keys");

            // ValidatorConfigPath doubles as the anchor LeanChainConfig and
            // ApplyBootstrapPeersFromNodesYaml use to find config.yaml and
            // nodes.yaml; the validator-config.yaml file itself is optional
            // under this layout.
            options.ValidatorConfigPath = Path.Combine(dir, "validator-config.yaml");

            if (string.IsNullOrWhiteSpace(options.Libp2p.PrivateKeyPath) &&
                !string.IsNullOrWhiteSpace(options.NodeName))
            {
                var nodeKeyPath = Path.Combine(dir, $"{options.NodeName}.key");
                if (File.Exists(nodeKeyPath))
                {
                    options.Libp2p.PrivateKeyPath = nodeKeyPath;
                }
            }
        }

        if (overrides.AggregateSubnetIds is { Length: > 0 })
        {
            options.Consensus.AggregateSubnetIds = overrides.AggregateSubnetIds;
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
    public string? PublicKeyHex { get; set; }
    public string? SecretKeyHex { get; set; }
    public string? PublicKeyPath { get; set; }
    public string? SecretKeyPath { get; set; }
    public ulong ValidatorIndex { get; set; }
    public ulong ValidatorCount { get; set; } = 1;
    public uint ActivationEpoch { get; set; }
    public uint NumActiveEpochs { get; set; } = 1024;
    public bool PublishAggregates { get; set; } = false;
}
