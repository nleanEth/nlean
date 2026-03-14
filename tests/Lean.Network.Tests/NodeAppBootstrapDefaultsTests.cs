using Lean.Metrics;
using Lean.Network;
using Lean.Node;
using Lean.Node.Configuration;
using Lean.Consensus;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Lean.Network.Tests;

[TestFixture]
public sealed class NodeAppBootstrapDefaultsTests
{
    private const string Enr0 =
        "enr:-IW4QOl1O0s_EnqwCuACxi91AAHK0utmnP30g8ZsGF_UkOJIDNHQzkaHhXkAioUK1gUHjL-P_PkYVr3EScebjZ0Wyd8BgmlkgnY0gmlwhH8AAAGEcXVpY4IjjYlzZWNwMjU2azGhA9TjdFfQ2XF6n-U-SCdT8ukmuQQUtZShaE5QcBVOAcp_";

    private const string Enr1 =
        "enr:-IW4QJwBZx9mbNOwm1fB6sFtDfLRbDHR8fs2rzmgx0X6ZzlzPtyo-4TChI0LF5TkL9o4XFEPSrx4bNwyHUiixYEzfaYBgmlkgnY0gmlwhH8AAAGEcXVpY4IjjolzZWNwMjU2azGhAhMMnGF1rmIPQ9tWgqfkNmvsG-aIyc9EJU5JFo3Tegys";

    private const string TwoValidatorsYaml =
        "validators:\n" +
        "  - name: nlean_0\n" +
        "    privkey: \"1111111111111111111111111111111111111111111111111111111111111111\"\n" +
        "    enrFields:\n" +
        "      ip: \"127.0.0.1\"\n" +
        "      quic: 9101\n" +
        "  - name: nlean_1\n" +
        "    privkey: \"2222222222222222222222222222222222222222222222222222222222222222\"\n" +
        "    enrFields:\n" +
        "      ip: \"127.0.0.1\"\n" +
        "      quic: 9102\n";

    private const string OneValidatorWithEnrYaml =
        "validators:\n" +
        "  - name: nlean_0\n" +
        "    privkey: \"1111111111111111111111111111111111111111111111111111111111111111\"\n" +
        "    enrFields:\n" +
        "      ip: \"127.0.0.1\"\n" +
        "      quic: 9101\n";

    private const string OneValidatorYaml =
        "validators:\n" +
        "  - name: nlean_0\n" +
        "    privkey: \"1111111111111111111111111111111111111111111111111111111111111111\"\n";

    [Test]
    public void Build_DoesNotInferBootstrapPeers_WhenBootstrapPeersAreEmpty_AndNodesYamlIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nlean-nodeapp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var validatorConfigPath = Path.Combine(tempRoot, "validator-config.yaml");
            File.WriteAllText(validatorConfigPath, TwoValidatorsYaml);

            var options = CreateOptions(tempRoot, validatorConfigPath);

            using var host = NodeApp.Build(options);

            Assert.That(options.Libp2p.BootstrapPeers, Is.Empty);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures from open handles on CI/dev machines.
            }
        }
    }

    [Test]
    public void Build_LoadsBootstrapPeersFromNodesYaml_WhenBootstrapPeersAreEmpty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nlean-nodeapp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var validatorConfigPath = Path.Combine(tempRoot, "validator-config.yaml");
            File.WriteAllText(validatorConfigPath, OneValidatorWithEnrYaml);
            File.WriteAllText(
                Path.Combine(tempRoot, "nodes.yaml"),
                "- " + Enr0 + "\n- " + Enr1 + "\n");

            var options = CreateOptions(tempRoot, validatorConfigPath);
            using var host = NodeApp.Build(options);

            Assert.That(options.Libp2p.BootstrapPeers, Has.Count.EqualTo(2));
            Assert.That(options.Libp2p.BootstrapPeers, Has.All.Contains("/quic-v1/p2p/"));
            Assert.That(options.Libp2p.BootstrapPeers, Has.All.Contains("/ip4/127.0.0.1/udp/"));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures from open handles on CI/dev machines.
            }
        }
    }

    [Test]
    public void Build_DoesNotOverrideConfiguredBootstrapPeers_WhenNodesYamlExists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nlean-nodeapp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var validatorConfigPath = Path.Combine(tempRoot, "validator-config.yaml");
            File.WriteAllText(validatorConfigPath, OneValidatorYaml);
            File.WriteAllText(
                Path.Combine(tempRoot, "nodes.yaml"),
                "- " + Enr0 + "\n");

            var options = CreateOptions(tempRoot, validatorConfigPath);
            options.Libp2p.BootstrapPeers.Add("/ip4/127.0.0.1/udp/9999/quic-v1/p2p/16Uiu2HAmJYwZZ");
            using var host = NodeApp.Build(options);

            Assert.That(options.Libp2p.BootstrapPeers, Has.Count.EqualTo(1));
            Assert.That(options.Libp2p.BootstrapPeers[0], Is.EqualTo("/ip4/127.0.0.1/udp/9999/quic-v1/p2p/16Uiu2HAmJYwZZ"));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures from open handles on CI/dev machines.
            }
        }
    }

    [Test]
    public void Build_RegistersConsensusServiceV2()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nlean-nodeapp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var validatorConfigPath = Path.Combine(tempRoot, "validator-config.yaml");
            File.WriteAllText(validatorConfigPath, OneValidatorYaml);

            var options = CreateOptions(tempRoot, validatorConfigPath);
            using var host = NodeApp.Build(options);

            var consensus = host.Services.GetRequiredService<IConsensusService>();
            Assert.That(consensus, Is.TypeOf<ConsensusServiceV2>());
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures from open handles on CI/dev machines.
            }
        }
    }

    private static NodeOptions CreateOptions(string tempRoot, string validatorConfigPath)
    {
        return new NodeOptions
        {
            ValidatorConfigPath = validatorConfigPath,
            NodeName = "nlean_0",
            DataDir = Path.Combine(tempRoot, "data"),
            Libp2p = new Libp2pConfig
            {
                BootstrapPeers = [],
                BootstrapNodeNames = [],
                EnableMdns = false,
                EnablePubsub = false,
                EnableQuic = false
            },
            Metrics = new MetricsConfig { Enabled = false }
        };
    }
}
