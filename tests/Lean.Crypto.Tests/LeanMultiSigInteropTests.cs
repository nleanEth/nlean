using Lean.Crypto;
using NUnit.Framework;

namespace Lean.Crypto.Tests;

public sealed class LeanMultiSigInteropTests
{
    [Test]
    public void AggregateAndVerifyRoundTrip()
    {
        try
        {
            var leanSig = new RustLeanSig();
            var leanMultiSig = new RustLeanMultiSig();
            leanMultiSig.SetupProver();
            leanMultiSig.SetupVerifier();

            var message = new byte[RustLeanSig.MessageLength];
            var epoch = 10u;

            var keyPairs = new List<LeanSigKeyPair>
            {
                leanSig.GenerateKeyPair(0, 16),
                leanSig.GenerateKeyPair(0, 16),
            };

            var signatures = keyPairs
                .Select(kp => (ReadOnlyMemory<byte>)leanSig.Sign(kp.SecretKey, epoch, message))
                .ToList();

            var publicKeys = keyPairs
                .Select(kp => (ReadOnlyMemory<byte>)kp.PublicKey)
                .ToList();

            var aggregate = leanMultiSig.AggregateSignatures(publicKeys, signatures, message, epoch);
            var isValid = leanMultiSig.VerifyAggregate(publicKeys, message, aggregate, epoch);

            Assert.That(isValid, Is.True);
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
