namespace Lean.Network;

public interface IGossipTopicProvider
{
    string ForkDigest { get; }

    string BlockTopic { get; }

    string AggregateTopic { get; }

    string AttestationSubnetTopic(int subnetId);
}
