namespace Lean.Network;

public sealed class Libp2pConfig
{
    public List<string> ListenAddresses { get; set; } = new() { "/ip4/0.0.0.0/tcp/0" };
    public List<string> BootstrapPeers { get; set; } = new();
    public List<string> BootstrapNodeNames { get; set; } = new();
    public Dictionary<string, string> PeerClientNames { get; set; } = new(StringComparer.Ordinal);
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyHex { get; set; }
    // mDNS multicast discovery is only useful for single-machine quickstart
    // workflows where bootstrap peers are unknown. In hive/CI/cloud and
    // nodes.yaml-driven devnets, peers are explicitly configured and mDNS
    // becomes harmful: it fires repeated peerStore.Discover() events for
    // already-connected peers, racing with the patched OutboundConnection
    // "replace" branch and causing rust-libp2p peers to drop our SUBSCRIBE
    // bookkeeping (see scripts/libp2p/patches/pubsub-anonymous.patch).
    // Default off; opt in for local quickstart via config or env.
    public bool EnableMdns { get; set; } = false;
    public bool EnablePubsub { get; set; } = true;
    public bool EnableQuic { get; set; } = true;
}
