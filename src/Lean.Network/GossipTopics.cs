namespace Lean.Network;

public static class GossipTopics
{
    public const string Prefix = "leanconsensus";
    public const string Encoding = "ssz_snappy";
    public const string DefaultNetwork = "devnet0";

    public static string Blocks => Block(DefaultNetwork);

    public static string Attestations => Attestation(DefaultNetwork);

    public static string Aggregates => Aggregate(DefaultNetwork);

    public static string Block(string network) => Format(network, "block");

    public static string Attestation(string network) => Format(network, "attestation");

    public static string Aggregate(string network) => Format(network, "aggregate");

    public static string AttestationSubnet(string network, int subnetId) => Format(network, $"attestation_{subnetId}");

    private static string Format(string network, string topic)
    {
        if (string.IsNullOrWhiteSpace(network))
        {
            network = DefaultNetwork;
        }

        return $"/{Prefix}/{network.Trim()}/{topic}/{Encoding}";
    }
}
