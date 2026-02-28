using Lean.Network;
using NUnit.Framework;

namespace Lean.Network.Tests;

[TestFixture]
public sealed class GossipTopicProviderTests
{
    [Test]
    public void Constructor_UsesConfiguredNetwork()
    {
        var provider = new GossipTopicProvider("devnet2");

        Assert.That(provider.Network, Is.EqualTo("devnet2"));
        Assert.That(provider.BlockTopic, Is.EqualTo("/leanconsensus/devnet2/block/ssz_snappy"));
        Assert.That(provider.AttestationTopic, Is.EqualTo("/leanconsensus/devnet2/attestation/ssz_snappy"));
        Assert.That(provider.AggregateTopic, Is.EqualTo("/leanconsensus/devnet2/aggregation/ssz_snappy"));
    }

    [Test]
    public void Constructor_UsesDefaultNetworkForBlankInput()
    {
        var provider = new GossipTopicProvider("  ");

        Assert.That(provider.Network, Is.EqualTo(GossipTopics.DefaultNetwork));
        Assert.That(provider.BlockTopic, Is.EqualTo(GossipTopics.Block(GossipTopics.DefaultNetwork)));
        Assert.That(provider.AttestationTopic, Is.EqualTo(GossipTopics.Attestation(GossipTopics.DefaultNetwork)));
    }

    [Test]
    public void Constructor_BuildsSingleNetworkTopics()
    {
        var provider = new GossipTopicProvider("devnet0");

        Assert.That(provider.BlockTopic, Is.EqualTo("/leanconsensus/devnet0/block/ssz_snappy"));
        Assert.That(provider.AttestationTopic, Is.EqualTo("/leanconsensus/devnet0/attestation/ssz_snappy"));
        Assert.That(provider.AggregateTopic, Is.EqualTo("/leanconsensus/devnet0/aggregation/ssz_snappy"));
    }
}
