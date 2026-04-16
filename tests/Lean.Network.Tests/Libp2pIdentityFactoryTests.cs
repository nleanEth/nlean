using Nethermind.Libp2p.Core.Dto;
using NUnit.Framework;

namespace Lean.Network.Tests;

[TestFixture]
public class Libp2pIdentityFactoryTests
{
    private const string KnownPrivateKeyHex = "2e9be3f1b0d32ca3a4d62017fbfafe3950b7e90fed6802ff8bd2e0f8c4e2ca91";
    private const string KnownPeerId = "16Uiu2HAmSz1wjLKPNn3DhMa7CYEg8o2V9kSSrQpeT8s4wJY3n6EJ";
    private const string HighBitPrivateKeyHex = "bdf953adc161873ba026330c56450453f582e3c4ee6cb713644794bcfdd85fe5";
    private const string HighBitPeerId = "16Uiu2HAkvi2sxT75Bpq1c7yV2FjnSQJJ432d6jeshbmfdJss1i6f";
    private const string ReamPrivateKeyHex = "af27950128b49cda7e7bc9fcb7b0270f7a3945aa7543326f3bfdbd57d2a97a32";
    private const string ReamPeerId = "16Uiu2HAmPQhkD6Zg5Co2ee8ShshkiY4tDePKFARPpCS2oKSLj1E1";

    [Test]
    public void Create_WithPrivateKeyHex_UsesExpectedPeerId()
    {
        var config = new Libp2pConfig
        {
            PrivateKeyHex = KnownPrivateKeyHex
        };

        var identity = Libp2pIdentityFactory.Create(config);

        Assert.That(identity.PeerId.ToString(), Is.EqualTo(KnownPeerId));
        Assert.That(identity.PrivateKey, Is.Not.Null);
        Assert.That(identity.PrivateKey!.Type, Is.EqualTo(KeyType.Secp256K1));
    }

    [Test]
    public void Create_WithPrivateKeyPath_UsesExpectedPeerId()
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

            Assert.That(identity.PeerId.ToString(), Is.EqualTo(KnownPeerId));
            Assert.That(identity.PrivateKey, Is.Not.Null);
            Assert.That(identity.PrivateKey!.Type, Is.EqualTo(KeyType.Secp256K1));
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
    public void Create_WithHighBitPrivateKeyHex_UsesExpectedPeerId()
    {
        var config = new Libp2pConfig
        {
            PrivateKeyHex = HighBitPrivateKeyHex
        };

        var identity = Libp2pIdentityFactory.Create(config);

        Assert.That(identity.PeerId.ToString(), Is.EqualTo(HighBitPeerId));
        Assert.That(identity.PrivateKey, Is.Not.Null);
        Assert.That(identity.PrivateKey!.Type, Is.EqualTo(KeyType.Secp256K1));
    }

    [Test]
    public void Create_WithReamPrivateKeyHex_UsesExpectedPeerId()
    {
        var config = new Libp2pConfig
        {
            PrivateKeyHex = ReamPrivateKeyHex
        };

        var identity = Libp2pIdentityFactory.Create(config);

        Assert.That(identity.PeerId.ToString(), Is.EqualTo(ReamPeerId));
        Assert.That(identity.PrivateKey, Is.Not.Null);
        Assert.That(identity.PrivateKey!.Type, Is.EqualTo(KeyType.Secp256K1));
    }
}
