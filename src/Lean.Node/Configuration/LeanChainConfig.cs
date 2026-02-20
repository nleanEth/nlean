using YamlDotNet.Serialization;

namespace Lean.Node.Configuration;

public sealed class LeanChainConfig
{
    [YamlMember(Alias = "GENESIS_TIME")]
    public ulong GenesisTime { get; set; }

    [YamlMember(Alias = "SECONDS_PER_SLOT")]
    public int? SecondsPerSlot { get; set; }

    [YamlMember(Alias = "VALIDATOR_COUNT")]
    public ulong? ValidatorCount { get; set; }

    [YamlMember(Alias = "GENESIS_VALIDATORS")]
    public List<string>? GenesisValidators { get; set; }

    public static LeanChainConfig? TryLoad(string validatorConfigPath)
    {
        if (string.IsNullOrWhiteSpace(validatorConfigPath))
        {
            return null;
        }

        var configDirectory = Path.GetDirectoryName(validatorConfigPath);
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            return null;
        }

        var chainConfigPath = Path.Combine(configDirectory, "config.yaml");
        if (!File.Exists(chainConfigPath))
        {
            return null;
        }

        var yaml = File.ReadAllText(chainConfigPath);
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<LeanChainConfig>(yaml);
    }
}
