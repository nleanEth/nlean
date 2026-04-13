using Lean.Crypto;
using NUnit.Framework;

namespace Lean.Crypto.Tests;

public sealed class ExternalQuickstartKeyInteropTests
{
    [Test]
    public void QuickstartValidator0AttesterKeySignAndVerify()
    {
        var runDir =
            Environment.GetEnvironmentVariable("NLEAN_QUICKSTART_RUN_DIR")
            ?? "/Users/grapebaba/Documents/projects/lean/nlean/vendor/lean-quickstart/local-devnet-nlean";

        var keyDir = Path.Combine(runDir, "genesis", "hash-sig-keys");
        var skPath = Path.Combine(keyDir, "validator_0_attester_key_sk.ssz");
        var pkPath = Path.Combine(keyDir, "validator_0_attester_key_pk.ssz");

        if (!File.Exists(skPath) || !File.Exists(pkPath))
        {
            Assert.Ignore("Quickstart attester key files not found (expected validator_0_attester_key_{sk,pk}.ssz).");
            return;
        }

        var sk = File.ReadAllBytes(skPath);
        var pk = File.ReadAllBytes(pkPath);

        var leanSig = new RustLeanSig();
        var msg = new byte[RustLeanSig.MessageLength];
        for (var i = 0; i < msg.Length; i++)
        {
            msg[i] = (byte)(i + 1);
        }

        const uint epoch = 1;
        var sig = leanSig.Sign(sk, epoch, msg);
        var ok = leanSig.Verify(pk, epoch, msg, sig);

        Assert.That(ok, Is.True, "nlean FFI failed to verify signature against quickstart attester key files.");
    }

}
