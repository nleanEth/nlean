namespace Lean.Client;

internal sealed class CliOptions
{
    public string? ConfigPath { get; set; }
    public string? DataDir { get; set; }
    public string? Network { get; set; }
    public bool? Metrics { get; set; }
    public string? LogLevel { get; set; }
    public string? ValidatorConfig { get; set; }
    public string? NodeName { get; set; }
    public string? CheckpointSyncUrl { get; set; }
    public string? NodeKeyPath { get; set; }
    public int? SocketPort { get; set; }
    public int? MetricsPort { get; set; }
    public string? MetricsAddress { get; set; }
    public bool IsAggregator { get; set; }
    public int[]? AggregateSubnetIds { get; set; }
    public int? AttestationCommitteeCount { get; set; }
    public int? ApiPort { get; set; }
    public string? HashSigKeyDir { get; set; }
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                options.ShowHelp = true;
                continue;
            }

            if (arg is "-v" or "--version")
            {
                options.ShowVersion = true;
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = arg.Substring(2).Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
            var key = parts[0];
            string? value = parts.Length > 1 ? parts[1] : null;

            // --is-aggregator is a pure flag (no value), so don't consume the next arg.
            if (key == "is-aggregator")
            {
                options.IsAggregator = true;
                continue;
            }

            if (value is null && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            switch (key)
            {
                case "config":
                    options.ConfigPath = value;
                    break;
                case "data-dir":
                    options.DataDir = value;
                    break;
                case "network":
                    options.Network = value;
                    break;
                case "metrics":
                    options.Metrics = value is null || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "log":
                    options.LogLevel = value;
                    break;
                case "validator-config":
                    options.ValidatorConfig = value;
                    break;
                case "node":
                case "node-id":
                    options.NodeName = value;
                    break;
                case "checkpoint-sync-url":
                    options.CheckpointSyncUrl = value;
                    break;
                case "node-key":
                    options.NodeKeyPath = value;
                    break;
                case "socket-port":
                    if (value is not null && int.TryParse(value, out var sp))
                        options.SocketPort = sp;
                    break;
                case "metrics-port":
                    if (value is not null && int.TryParse(value, out var mp))
                        options.MetricsPort = mp;
                    break;
                case "metrics-address":
                    options.MetricsAddress = value;
                    break;
                case "attestation-committee-count":
                    if (value is not null && int.TryParse(value, out var acc))
                        options.AttestationCommitteeCount = acc;
                    break;
                case "api-port":
                    if (value is not null && int.TryParse(value, out var ap))
                        options.ApiPort = ap;
                    break;
                case "hash-sig-key-dir":
                    options.HashSigKeyDir = value;
                    break;
                case "aggregate-subnet-ids":
                    if (value is not null)
                    {
                        var subnetParts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var ids = new List<int>();
                        foreach (var part in subnetParts)
                        {
                            if (int.TryParse(part, out var id))
                                ids.Add(id);
                        }
                        options.AggregateSubnetIds = ids.ToArray();
                    }
                    break;
            }
        }

        return options;
    }
}
