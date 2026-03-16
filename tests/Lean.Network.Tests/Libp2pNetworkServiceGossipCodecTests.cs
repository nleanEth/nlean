using System.Reflection;
using NUnit.Framework;
using Snappier;

namespace Lean.Network.Tests;

[TestFixture]
public sealed class Libp2pNetworkServiceGossipCodecTests
{
    [Test]
    public void EncodeGossipPayload_CompressesPayloadWithSnappy()
    {
        var rawPayload = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x10, 0x20, 0x30, 0x40 };

        var encoded = InvokeEncode(rawPayload);

        Assert.That(encoded, Is.Not.EqualTo(rawPayload), "gossip payload must not be published as raw SSZ bytes");
        Assert.That(Snappy.DecompressToArray(encoded), Is.EqualTo(rawPayload));
    }

    [Test]
    public void DecodeGossipPayload_RequiresSnappyPayload()
    {
        var rawPayload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
        var compressed = Snappy.CompressToArray(rawPayload);

        var decodedCompressed = InvokeDecode(compressed);
        Assert.That(decodedCompressed, Is.EqualTo(rawPayload));
        Assert.That(
            () => InvokeDecode(rawPayload),
            Throws.TypeOf<TargetInvocationException>()
                .With.InnerException.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ShouldInitiateBootstrapDial_OnlyOneSideDialsForSamePeerPair()
    {
        const string peerA = "/ip4/127.0.0.1/udp/9101/quic-v1/p2p/16Uiu2HAmSz1wjLKPNn3DhMa7CYEg8o2V9kSSrQpeT8s4wJY3n6EJ";
        const string peerB = "/ip4/127.0.0.1/udp/9102/quic-v1/p2p/16Uiu2HAkvi2sxT75Bpq1c7yV2FjnSQJJ432d6jeshbmfdJss1i6f";
        const string localA = "16Uiu2HAmSz1wjLKPNn3DhMa7CYEg8o2V9kSSrQpeT8s4wJY3n6EJ";
        const string localB = "16Uiu2HAkvi2sxT75Bpq1c7yV2FjnSQJJ432d6jeshbmfdJss1i6f";

        var aDialsB = InvokeShouldInitiateBootstrapDial(localA, peerB);
        var bDialsA = InvokeShouldInitiateBootstrapDial(localB, peerA);

        Assert.That(aDialsB ^ bDialsA, Is.True, "exactly one side should initiate the bootstrap dial");
    }

    [Test]
    public void NormalizePeerIdentityKey_UsesPeerId_WhenPeerIdExists()
    {
        const string peerKeyA = "/ip4/127.0.0.1/udp/19601/quic-v1/p2p/16Uiu2HAmPeerA";
        const string peerKeyB = "/ip4/127.0.0.1/udp/29601/quic-v1/p2p/16Uiu2HAmPeerA";

        var normalizedA = InvokeNormalizePeerIdentityKey(peerKeyA);
        var normalizedB = InvokeNormalizePeerIdentityKey(peerKeyB);

        Assert.That(normalizedA, Is.EqualTo("16Uiu2HAmPeerA"));
        Assert.That(normalizedB, Is.EqualTo("16Uiu2HAmPeerA"));
    }

    [Test]
    public void NormalizePeerIdentityKey_FallsBackToTrimmedKey_WhenPeerIdMissing()
    {
        const string peerKey = " /ip4/127.0.0.1/udp/19601/quic-v1 ";

        var normalized = InvokeNormalizePeerIdentityKey(peerKey);

        Assert.That(normalized, Is.EqualTo("/ip4/127.0.0.1/udp/19601/quic-v1"));
    }

    private static byte[] InvokeEncode(ReadOnlyMemory<byte> payload)
    {
        var method = typeof(Libp2pNetworkService).GetMethod(
            "EncodeGossipPayload",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        return (byte[])method!.Invoke(null, new object[] { payload })!;
    }

    private static byte[] InvokeDecode(byte[] payload)
    {
        var method = typeof(Libp2pNetworkService).GetMethod(
            "DecodeGossipPayload",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        return (byte[])method!.Invoke(null, new object[] { payload })!;
    }

    private static bool InvokeShouldInitiateBootstrapDial(string localPeerId, string peerKey)
    {
        var method = typeof(Libp2pNetworkService).GetMethod(
            "ShouldInitiateBootstrapDial",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        return (bool)method!.Invoke(null, new object[] { localPeerId, peerKey })!;
    }

    private static string InvokeNormalizePeerIdentityKey(string peerKey)
    {
        var method = typeof(Libp2pNetworkService).GetMethod(
            "NormalizePeerIdentityKey",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.That(method, Is.Not.Null);
        return (string)method!.Invoke(null, new object[] { peerKey })!;
    }
}
