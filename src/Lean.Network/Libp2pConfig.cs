namespace Lean.Network;

public sealed class Libp2pConfig
{
    public List<string> ListenAddresses { get; set; } = new() { "/ip4/0.0.0.0/tcp/0" };
    public List<string> BootstrapPeers { get; set; } = new();
    public bool EnableMdns { get; set; } = true;
    public bool EnablePubsub { get; set; } = true;
    public bool EnableQuic { get; set; } = true;
}
