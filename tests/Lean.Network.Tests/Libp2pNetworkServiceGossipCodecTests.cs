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
}
