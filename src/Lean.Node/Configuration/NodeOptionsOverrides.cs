namespace Lean.Node.Configuration;

public sealed record NodeOptionsOverrides(
    string? ConfigPath,
    string? DataDir,
    string? Network,
    bool? MetricsEnabled,
    string? Libp2pConfig,
    string? LogLevel,
    string? ValidatorConfigPath,
    string? NodeName);
