namespace Lean.Network;

public static class GossipTopics
{
    public const string Prefix = "leanconsensus";
    public const string Encoding = "ssz_snappy";
    /// Fork digest embedded in gossipsub topic strings, as lowercase hex
    /// without 0x prefix. Currently a dummy value shared across all clients;
    /// will eventually be derived from fork version + genesis validators root.
    public const string DefaultForkDigest = "12345678";


    public static string Blocks => Block(DefaultForkDigest);

    public static string Aggregates => Aggregate(DefaultForkDigest);

    public static string Block(string forkDigest) => Format(forkDigest, "block");

    public static string Aggregate(string forkDigest) => Format(forkDigest, "aggregation");

    public static string AttestationSubnet(string forkDigest, int subnetId) => Format(forkDigest, $"attestation_{subnetId}");

    private static string Format(string forkDigest, string topic)
    {
        if (string.IsNullOrWhiteSpace(forkDigest))
        {
            forkDigest = DefaultForkDigest;
        }

        return $"/{Prefix}/{forkDigest.Trim()}/{topic}/{Encoding}";
    }
}
