namespace Lean.Network;

public interface IGossipTopicProvider
{
    string Network { get; }

    string BlockTopic { get; }

    string AttestationTopic { get; }

    string AggregateTopic { get; }
}
