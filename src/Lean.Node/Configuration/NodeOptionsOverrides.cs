namespace Lean.Node.Configuration;

public sealed record NodeOptionsOverrides(
    string? ConfigPath,
    string? DataDir,
    string? ForkDigest,
    bool? MetricsEnabled,
    string? LogLevel,
    string? ValidatorConfigPath,
    string? NodeName,
    string? CheckpointSyncUrl = null,
    string? NodeKeyPath = null,
    int? SocketPort = null,
    int? MetricsPort = null,
    string? MetricsAddress = null,
    bool IsAggregator = false,
    int? AttestationCommitteeCount = null,
    int? ApiPort = null,
    string? HashSigKeyDir = null,
    int[]? AggregateSubnetIds = null,
    string? AnnotatedValidatorsPath = null);
