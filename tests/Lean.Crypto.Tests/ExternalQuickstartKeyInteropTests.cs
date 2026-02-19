using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Crypto;
using NUnit.Framework;

namespace Lean.Crypto.Tests;

public sealed class ExternalQuickstartKeyInteropTests
{
    [Test]
    public void QuickstartValidator0KeysSignAndVerify()
    {
        const string basePath = "/Users/grapebaba/conductor/workspaces/zeam/lima-v1/lean-quickstart/local-devnet-nlean-investigate16/genesis/hash-sig-keys";
        var skPath = Path.Combine(basePath, "validator_0_sk.ssz");
        var pkPath = Path.Combine(basePath, "validator_0_pk.ssz");

        if (!File.Exists(skPath) || !File.Exists(pkPath))
        {
            Assert.Ignore("Quickstart key files not found.");
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

        Assert.That(ok, Is.True, "nlean FFI failed to verify signature against quickstart key files.");
    }

    [Test]
    public void DumpedQuickstartAttestationsVerifyWithValidator0PublicKey()
    {
        var runDir =
            Environment.GetEnvironmentVariable("NLEAN_QUICKSTART_RUN_DIR")
            ?? "/Users/grapebaba/conductor/workspaces/zeam/lima-v1/lean-quickstart/local-devnet-nlean-investigate19";

        var pkPath = Path.Combine(runDir, "genesis", "hash-sig-keys", "validator_0_pk.ssz");
        var dumpDir = Path.Combine(runDir, "data", "nlean_0", "att-debug");

        if (!File.Exists(pkPath) || !Directory.Exists(dumpDir))
        {
            Assert.Ignore("Quickstart attestation dump or validator_0 public key file not found.");
            return;
        }

        var payloadPaths = Directory.GetFiles(dumpDir, "*.ssz", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (payloadPaths.Length == 0)
        {
            Assert.Ignore("No dumped attestation payloads found.");
            return;
        }

        var pk = File.ReadAllBytes(pkPath);
        var leanSig = new RustLeanSig();
        var decoder = new SignedAttestationGossipDecoder();

        foreach (var payloadPath in payloadPaths)
        {
            var payload = File.ReadAllBytes(payloadPath);
            var decodeResult = decoder.DecodeAndValidate(payload);
            Assert.That(
                decodeResult.Failure,
                Is.EqualTo(AttestationGossipDecodeFailure.None),
                $"failed to decode attestation payload: {Path.GetFileName(payloadPath)}");

            var signedAttestation = decodeResult.Attestation!;
            var root = signedAttestation.Message.HashTreeRoot();
            var signature = signedAttestation.Signature.EncodeBytes();
            var epoch = checked((uint)signedAttestation.Message.Slot.Value);

            var ok = leanSig.Verify(pk, epoch, root, signature);
            Assert.That(
                ok,
                Is.True,
                $"signature verification failed for payload {Path.GetFileName(payloadPath)} (slot={signedAttestation.Message.Slot.Value})");
        }
    }
}
