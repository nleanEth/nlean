using Lean.Crypto;
using NUnit.Framework;

namespace Lean.Crypto.Tests;

public sealed class LeanSigInteropTests
{
    [Test]
    public void KeyGenSignVerifyRoundTrip()
    {
        try
        {
            var leanSig = new RustLeanSig();
            var keyPair = leanSig.GenerateKeyPair(0, 10);
            var message = new byte[RustLeanSig.MessageLength];
            var signature = leanSig.Sign(keyPair.SecretKey, 1, message);

            Assert.That(leanSig.Verify(keyPair.PublicKey, 1, message, signature), Is.True);
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
