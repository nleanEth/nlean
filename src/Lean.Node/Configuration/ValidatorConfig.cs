using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lean.Node.Configuration;

public sealed class ValidatorConfig
{
    public string? Shuffle { get; set; }
    [YamlMember(Alias = "deployment_mode")]
    public string? DeploymentMode { get; set; }
    public ValidatorChainConfig? Config { get; set; }
    public List<ValidatorNodeConfig> Validators { get; set; } = new();

    public static ValidatorConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"validator-config.yaml not found: {path}");
        }

        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var config = deserializer.Deserialize<ValidatorConfig>(yaml) ?? new ValidatorConfig();
        config.Validators ??= new List<ValidatorNodeConfig>();
        return config;
    }

    public ValidatorNodeConfig? FindNode(string? nodeName)
    {
        if (string.IsNullOrWhiteSpace(nodeName))
        {
            return Validators.FirstOrDefault();
        }

        return Validators.FirstOrDefault(v => string.Equals(v.Name, nodeName, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ValidatorChainConfig
{
    public int ActiveEpoch { get; set; }
    public string? KeyType { get; set; }
}

public sealed class ValidatorNodeConfig
{
    public string Name { get; set; } = string.Empty;
    public string? Privkey { get; set; }
    public EnrFields? EnrFields { get; set; }
    public int MetricsPort { get; set; }
    public int Count { get; set; } = 1;
    public int? Devnet { get; set; }
}

public sealed class EnrFields
{
    public string? Ip { get; set; }
    public int? Quic { get; set; }
}
