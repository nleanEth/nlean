using Lean.Consensus.Types;
using Lean.Crypto;
using NUnit.Framework;

namespace Lean.Crypto.Tests;

public sealed class LeanSigXmssInteropTests
{
    [Test]
    public void SignaturesAreFixedLengthAndCompatibleWithConsensusXmss()
    {
        try
        {
            var leanSig = new RustLeanSig();
            var keyPair = leanSig.GenerateKeyPair(0, 10);
            var message = new byte[RustLeanSig.MessageLength];
            var signature = leanSig.Sign(keyPair.SecretKey, 1, message);

            Assert.That(signature.Length, Is.EqualTo(XmssSignature.Length));

            var xmss = XmssSignature.FromBytes(signature);
            var signedAttestation = new SignedAttestation(
                0,
                new AttestationData(
                    new Slot(0),
                    Checkpoint.Default(),
                    Checkpoint.Default(),
                    Checkpoint.Default()),
                xmss);

            var encoded = SszEncoding.Encode(signedAttestation);
            var signatureOffset = encoded.Length - XmssSignature.Length;

            Assert.That(encoded.AsSpan(signatureOffset, XmssSignature.Length).ToArray(),
                Is.EqualTo(signature));
        }
        catch (DllNotFoundException)
        {
            Assert.Ignore("Native lean crypto library not found. Build native bindings first.");
        }
        catch (BadImageFormatException)
        {
            Assert.Ignore("Native lean crypto library incompatible with current platform.");
        }
    }
}
