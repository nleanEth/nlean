namespace Lean.Network;

public interface IGossipTopicProvider
{
    string Network { get; }

    string BlockTopic { get; }

    string AggregateTopic { get; }

    string AttestationSubnetTopic(int subnetId);
}
