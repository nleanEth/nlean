namespace Lean.Network;

public sealed class GossipTopicProvider : IGossipTopicProvider
{
    public GossipTopicProvider(string? forkDigest)
    {
        ForkDigest = NormalizeForkDigest(forkDigest);
        BlockTopic = GossipTopics.Block(ForkDigest);
        AggregateTopic = GossipTopics.Aggregate(ForkDigest);
    }

    public string ForkDigest { get; }

    public string BlockTopic { get; }

    public string AggregateTopic { get; }

    public string AttestationSubnetTopic(int subnetId) => GossipTopics.AttestationSubnet(ForkDigest, subnetId);

    private static string NormalizeForkDigest(string? forkDigest)
    {
        if (string.IsNullOrWhiteSpace(forkDigest))
        {
            return GossipTopics.DefaultForkDigest;
        }

        return forkDigest.Trim();
    }
}
