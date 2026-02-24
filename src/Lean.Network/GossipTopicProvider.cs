namespace Lean.Network;

public sealed class GossipTopicProvider : IGossipTopicProvider
{
    public GossipTopicProvider(string? network)
    {
        Network = NormalizeNetwork(network);
        BlockTopic = GossipTopics.Block(Network);
        AttestationTopic = GossipTopics.Attestation(Network);
        AggregateTopic = GossipTopics.Aggregate(Network);
    }

    public string Network { get; }

    public string BlockTopic { get; }

    public string AttestationTopic { get; }

    public string AggregateTopic { get; }

    public string AttestationSubnetTopic(int subnetId) => GossipTopics.AttestationSubnet(Network, subnetId);

    private static string NormalizeNetwork(string? network)
    {
        if (string.IsNullOrWhiteSpace(network))
        {
            return GossipTopics.DefaultNetwork;
        }

        return network.Trim();
    }
}
