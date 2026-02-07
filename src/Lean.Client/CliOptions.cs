namespace Lean.Client;

internal sealed class CliOptions
{
    public string? ConfigPath { get; set; }
    public string? DataDir { get; set; }
    public string? Network { get; set; }
    public bool? Metrics { get; set; }
    public string? Libp2pConfig { get; set; }
    public string? LogLevel { get; set; }
    public string? ValidatorConfig { get; set; }
    public string? NodeName { get; set; }
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
                case "libp2p":
                    options.Libp2pConfig = value;
                    break;
                case "log":
                    options.LogLevel = value;
                    break;
                case "validator-config":
                    options.ValidatorConfig = value;
                    break;
                case "node":
                    options.NodeName = value;
                    break;
            }
        }

        return options;
    }
}
