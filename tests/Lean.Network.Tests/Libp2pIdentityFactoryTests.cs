using Nethermind.Libp2p.Core.Dto;
using NUnit.Framework;

namespace Lean.Network.Tests;

[TestFixture]
public class Libp2pIdentityFactoryTests
{
    private const string KnownPrivateKeyHex = "2e9be3f1b0d32ca3a4d62017fbfafe3950b7e90fed6802ff8bd2e0f8c4e2ca91";

    [Test]
    public void Create_WithPrivateKeyHex_ProducesEd25519Identity()
    {
        var config = new Libp2pConfig
        {
            PrivateKeyHex = KnownPrivateKeyHex
        };

        var identity = Libp2pIdentityFactory.Create(config);

        Assert.That(identity.PeerId.ToString(), Is.Not.Empty);
        Assert.That(identity.PrivateKey, Is.Not.Null);
        Assert.That(identity.PrivateKey!.Type, Is.EqualTo(KeyType.Ed25519));
    }

    [Test]
    public void Create_WithPrivateKeyPath_ProducesEd25519Identity()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.key");
        try
        {
            File.WriteAllText(filePath, $"{KnownPrivateKeyHex}\n");

            var config = new Libp2pConfig
            {
                PrivateKeyPath = filePath
            };

            var identity = Libp2pIdentityFactory.Create(config);

            Assert.That(identity.PeerId.ToString(), Is.Not.Empty);
            Assert.That(identity.PrivateKey, Is.Not.Null);
            Assert.That(identity.PrivateKey!.Type, Is.EqualTo(KeyType.Ed25519));
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Test]
    public void Create_WithInvalidPrivateKeyHex_Throws()
    {
        var config = new Libp2pConfig
        {
            PrivateKeyHex = "not-hex"
        };

        Assert.That(() => Libp2pIdentityFactory.Create(config), Throws.InvalidOperationException);
    }

    [Test]
    public void Create_SameKey_ProducesSamePeerId()
    {
        var id1 = Libp2pIdentityFactory.CreateFromHex(KnownPrivateKeyHex, "test");
        var id2 = Libp2pIdentityFactory.CreateFromHex(KnownPrivateKeyHex, "test");

        Assert.That(id1.PeerId.ToString(), Is.EqualTo(id2.PeerId.ToString()));
    }

    [Test]
    public void Create_DifferentKeys_ProduceDifferentPeerIds()
    {
        var id1 = Libp2pIdentityFactory.CreateFromHex(
            "2e9be3f1b0d32ca3a4d62017fbfafe3950b7e90fed6802ff8bd2e0f8c4e2ca91", "test1");
        var id2 = Libp2pIdentityFactory.CreateFromHex(
            "64a7f5ab53907966374ca23af36392910af682eec82c12e3abbb6c2ccdf39a72", "test2");

        Assert.That(id1.PeerId.ToString(), Is.Not.EqualTo(id2.PeerId.ToString()));
    }

    [Test]
    public void Create_HighBitKey_ProducesEd25519Identity()
    {
        var id = Libp2pIdentityFactory.CreateFromHex(
            "bdf953adc161873ba026330c56450453f582e3c4ee6cb713644794bcfdd85fe5", "test");

        Assert.That(id.PrivateKey!.Type, Is.EqualTo(KeyType.Ed25519));
        Assert.That(id.PeerId.ToString(), Is.Not.Empty);
    }
}
