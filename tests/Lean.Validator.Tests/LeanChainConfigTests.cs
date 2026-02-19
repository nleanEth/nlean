using Lean.Node.Configuration;
using NUnit.Framework;

namespace Lean.Validator.Tests;

[TestFixture]
public sealed class LeanChainConfigTests
{
    [Test]
    public void TryLoad_ReadsGenesisTimeFromUppercaseKeys()
    {
        using var tempDir = new TempDirectory();
        var validatorConfigPath = Path.Combine(tempDir.Path, "validator-config.yaml");
        var chainConfigPath = Path.Combine(tempDir.Path, "config.yaml");

        File.WriteAllText(validatorConfigPath, "validators: []");
        File.WriteAllText(chainConfigPath, "GENESIS_TIME: 1770627152\nSECONDS_PER_SLOT: 4\nVALIDATOR_COUNT: 3\n");

        var config = LeanChainConfig.TryLoad(validatorConfigPath);

        Assert.That(config, Is.Not.Null);
        Assert.That(config!.GenesisTime, Is.EqualTo(1770627152UL));
        Assert.That(config.SecondsPerSlot, Is.EqualTo(4));
        Assert.That(config.ValidatorCount, Is.EqualTo(3UL));
    }

    [Test]
    public void TryLoad_ReadsGenesisValidatorsList()
    {
        using var tempDir = new TempDirectory();
        var validatorConfigPath = Path.Combine(tempDir.Path, "validator-config.yaml");
        var chainConfigPath = Path.Combine(tempDir.Path, "config.yaml");

        File.WriteAllText(validatorConfigPath, "validators: []");
        File.WriteAllText(chainConfigPath, "GENESIS_VALIDATORS:\n  - \"0x01\"\n  - \"0x02\"\n");

        var config = LeanChainConfig.TryLoad(validatorConfigPath);

        Assert.That(config, Is.Not.Null);
        Assert.That(config!.GenesisValidators, Is.Not.Null);
        Assert.That(config.GenesisValidators, Has.Count.EqualTo(2));
    }

    [Test]
    public void TryLoad_ReturnsNullWhenChainConfigMissing()
    {
        using var tempDir = new TempDirectory();
        var validatorConfigPath = Path.Combine(tempDir.Path, "validator-config.yaml");
        File.WriteAllText(validatorConfigPath, "validators: []");

        var config = LeanChainConfig.TryLoad(validatorConfigPath);

        Assert.That(config, Is.Null);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nlean-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
