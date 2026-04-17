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

        Assert.That(provider.ForkDigest, Is.EqualTo("devnet2"));
        Assert.That(provider.BlockTopic, Is.EqualTo("/leanconsensus/devnet2/block/ssz_snappy"));
        Assert.That(provider.AggregateTopic, Is.EqualTo("/leanconsensus/devnet2/aggregation/ssz_snappy"));
    }

    [Test]
    public void Constructor_UsesDefaultNetworkForBlankInput()
    {
        var provider = new GossipTopicProvider("  ");

        Assert.That(provider.ForkDigest, Is.EqualTo(GossipTopics.DefaultForkDigest));
        Assert.That(provider.BlockTopic, Is.EqualTo(GossipTopics.Block(GossipTopics.DefaultForkDigest)));
    }

    [Test]
    public void Constructor_BuildsSingleNetworkTopics()
    {
        var provider = new GossipTopicProvider("12345678");

        Assert.That(provider.BlockTopic, Is.EqualTo("/leanconsensus/12345678/block/ssz_snappy"));
        Assert.That(provider.AggregateTopic, Is.EqualTo("/leanconsensus/12345678/aggregation/ssz_snappy"));
    }
}
