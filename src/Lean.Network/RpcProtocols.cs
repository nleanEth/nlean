namespace Lean.Network;

public static class RpcProtocols
{
    public const string BlocksByRoot = "/leanconsensus/req/blocks_by_root/1/ssz_snappy";
    public const string BlocksByRange = "/leanconsensus/req/blocks_by_range/1/ssz_snappy";
    public const string Status = "/leanconsensus/req/status/1/ssz_snappy";
}
